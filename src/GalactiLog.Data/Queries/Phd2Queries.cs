using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// One LIGHT frame the correlation of spec 7.6 may fill: it has a capture time, a positive
/// exposure, no guiding RMS, and a <c>guiding_rms_source</c> that is not <c>csv</c>.
/// </summary>
/// <param name="Id"><c>images.id</c>, the scope of one fill write.</param>
/// <param name="CaptureUtc"><c>capture_date</c>, a UTC instant stored with no kind. Never given a
/// <see cref="DateTimeKind"/> here: the arithmetic is a difference of ticks, which a kind does not
/// enter.</param>
/// <param name="ExposureSeconds"><c>exposure_time</c>, always greater than zero by the filter.
/// </param>
/// <param name="Telescope">The raw stored spelling, which is what a rig is keyed by.</param>
public sealed record Phd2FillCandidate(
    Guid Id, DateTime CaptureUtc, double ExposureSeconds, string? Telescope);

/// <summary>
/// One guiding session as the correlation reads it: the six columns the pass needs and no more.
/// </summary>
/// <param name="Id"><c>phd2_sessions.id</c>.</param>
/// <param name="Telescope">Resolved from the profile map at ingest. Null means unmapped.</param>
/// <param name="EquipmentProfile">The header's profile name. Null on an ASIAIR section.</param>
/// <param name="PixelScaleArcsec">Null on an ASIAIR section, which is never correlated.</param>
/// <param name="StartedAtUtc">Never null here: a session with no zone has no UTC instant
/// (ruling F1) and is therefore not reachable by the overlap query at all.</param>
/// <param name="EndedAtUtc">Null for a truncated section.</param>
/// <param name="Events">The stored <c>events</c> JSON array, which the one dither and settle
/// window rule is fed from.</param>
public sealed record Phd2SessionRow(
    Guid Id,
    string? Telescope,
    string? EquipmentProfile,
    double? PixelScaleArcsec,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string Events);

/// <summary>One stored guide frame, in the five columns the pooled sample stream is built from.
/// </summary>
public sealed record Phd2FrameRow(
    Guid SessionId, double TimeOffset, double? RaRaw, double? DecRaw, bool Dropped);

/// <summary>
/// The reads the correlation of spec 7.6 makes, and nothing else. <b>This type writes nothing.</b>
/// Every write of a guiding column in this solution goes through
/// <c>Phd2Correlation.WriteGuiding</c>, which is what makes the never-overwrite guarantee of
/// ruling F6 structural rather than conventional.
/// </summary>
/// <remarks>
/// <para>
/// Every member takes the caller's <see cref="GalactiLogContext"/> rather than a connection
/// string. Spec 5.1's single-writer contract: the pass opens one connection and these reads share
/// it.
/// </para>
/// <para>
/// THIS TYPE PERFORMS NO FILESYSTEM ACCESS AT ALL. It names no <c>System.IO</c> member.
/// </para>
/// </remarks>
public static class Phd2Queries
{
    // Bounds a generated IN (...) list, the same 500 Phd2Repository and OrphanPruner use for the
    // same reason: an unbounded list over a corpus exceeds SQLite's parameter limit and fails the
    // whole read rather than a batch of it.
    private const int SessionBatchSize = 500;

    // Spec 7.5: image_type is stored trimmed and upper-cased by MetadataExtractor and a frame with
    // no IMAGETYP is stored as LIGHT, so an exact match is the whole rule. SqlFragments.
    // LightFrameOnly says the same thing in SQL text for the dashboard queries; this is the LINQ
    // side of the one rule, and the two are asserted against each other by nothing, which is why
    // the sentence is repeated here rather than left implied.
    private const string LightImageType = "LIGHT";

    /// <summary>
    /// Spec 7.6's incremental mode: the nights holding a LIGHT frame with no guiding RMS at all
    /// that a guiding session could plausibly cover. Two set-based distinct scans over indexed
    /// date columns and an intersection, never a walk over every night in the catalogue: a library
    /// with ten years of frames and one month of guide logs must not pay for the other nine years
    /// on every scan.
    /// </summary>
    /// <remarks>
    /// The guide side is widened by a day either way. A guiding session's own <c>session_date</c>
    /// lands a day off the frames it covers whenever the longitude behind it is unset, and the
    /// per-night session lookup is a time overlap that would find it, but only if the night is
    /// visited at all.
    /// </remarks>
    public static IReadOnlyList<DateOnly> NightsNeedingFill(GalactiLogContext context)
    {
        // The cheap half first. The images scan below touches every LIGHT frame with no guiding
        // RMS, which on a library with ten years of frames and no guide logs is the whole frame
        // catalogue, and it was being paid on every scan and every settings save before the guide
        // side was consulted at all (phase review P3-1). With no guiding session stored there is
        // no night to intersect with and the answer is empty whatever the frame side says.
        var widened = new HashSet<DateOnly>();
        foreach (var night in GuideNights(context))
        {
            widened.UnionWith(NightDates(night));
        }

        if (widened.Count == 0)
        {
            return [];
        }

        var frameNights = context.Images
            .Where(i => i.SessionDate != null
                && i.CaptureDate != null
                && i.ExposureTime > 0
                && i.ImageType == LightImageType
                && i.GuidingRmsArcsec == null)
            .Where(Phd2Correlation.NeverOverwritesCsv)
            .Select(i => i.SessionDate!.Value)
            .Distinct()
            .ToHashSet();

        frameNights.IntersectWith(widened);
        return [.. frameNights.Order()];
    }

    /// <summary>
    /// Spec 7.6: the nights a settings change invalidates, which is the union of the nights
    /// holding a <c>phd2</c>-sourced frame, whose attribution may now be wrong, the nights
    /// holding a guiding session, which may now attribute where it did not before, and the nights
    /// an unzoned session could be about (<see cref="UnzonedCandidateNights"/>). Bounded by the
    /// guide-log corpus rather than by the size of the frame catalogue.
    /// </summary>
    public static IReadOnlyList<DateOnly> InvalidatedNights(GalactiLogContext context)
    {
        var nights = context.Images
            .Where(i => i.SessionDate != null && i.GuidingRmsSource == Phd2Correlation.Phd2Source)
            .Select(i => i.SessionDate!.Value)
            .Distinct()
            .ToHashSet();
        nights.UnionWith(GuideNights(context));
        nights.UnionWith(UnzonedCandidateNights(context));
        return [.. nights.Order()];
    }

    /// <summary>
    /// The nights an unzoned session could be about, for the third half of
    /// <see cref="InvalidatedNights"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruling F1 leaves an unzoned session a null <c>started_at_utc</c> AND a null
    /// <c>session_date</c>, so <see cref="GuideNights"/> cannot reach it and neither can the
    /// <c>phd2</c>-sourced frame half. Without this the nights such a session touches are in no
    /// half of the union, a settings-triggered pass visits none of them, and
    /// <c>phd2_timezone_unset</c> can never name exactly the profiles it is about: the one warning
    /// that tells the user why their guiding is missing is unreachable from the path a zone edit
    /// takes.
    /// </para>
    /// <para>
    /// Its non-nullable <c>started_at_local</c>, widened by a day either way, is the only stored
    /// key it has, and it is sound because no zone shifts a wall clock by more than about fourteen
    /// hours (spec 7.6). The widened set is intersected with the nights that actually hold frames,
    /// so a night with nothing on it is neither visited nor warned about, and a corpus of guide
    /// logs with no images at all adds nothing here.
    /// </para>
    /// <para>
    /// Only the unzoned rows are drawn down, which is empty on a correctly configured library, and
    /// the date arithmetic runs in memory because <c>DateOnly.FromDateTime</c> has no SQLite
    /// translation.
    /// </para>
    /// </remarks>
    private static IEnumerable<DateOnly> UnzonedCandidateNights(GalactiLogContext context)
    {
        var wanted = new HashSet<DateOnly>();
        var locals = context.Phd2Sessions
            .Where(s => s.StartedAtUtc == null)
            .Select(s => s.StartedAtLocal)
            .Distinct()
            .ToList();
        foreach (var local in locals.Select(DateOnly.FromDateTime))
        {
            wanted.UnionWith(NightDates(local));
        }

        if (wanted.Count == 0)
        {
            return [];
        }

        wanted.IntersectWith(context.Images
            .Where(i => i.SessionDate != null)
            .Select(i => i.SessionDate!.Value)
            .Distinct()
            .ToList());
        return wanted;
    }

    /// <summary>
    /// One night's fill candidates. <b>A frame whose <c>guiding_rms_source</c> is <c>csv</c> is
    /// never returned</b>: spec 7.6's guarantee covers reading as a candidate as well as writing,
    /// and the predicate that enforces it is the same one the write statement carries.
    /// </summary>
    public static IReadOnlyList<Phd2FillCandidate> UnfilledLights(
        GalactiLogContext context, DateOnly night)
        => context.Images
            .Where(i => i.SessionDate == night
                && i.ImageType == LightImageType
                && i.CaptureDate != null
                && i.ExposureTime > 0
                && i.GuidingRmsArcsec == null)
            .Where(Phd2Correlation.NeverOverwritesCsv)
            .Select(i => new Phd2FillCandidate(
                i.Id, i.CaptureDate!.Value, i.ExposureTime!.Value, i.Telescope))
            .ToList();

    /// <summary>
    /// The night's <b>full</b> rig set, the raw stored telescope of every LIGHT frame on it,
    /// whether or not that frame still needs a value.
    /// </summary>
    /// <remarks>
    /// This is what the multi-rig veto of spec 7.6 is counted over, and it is deliberately not the
    /// unfilled subset. A rig whose frames all already carry a CSV value is invisible to
    /// <see cref="UnfilledLights"/>, and once it drops out of the count a two-rig night reads as
    /// single-rig: the sole-unmapped-profile fallback stops being vetoed and the CSV-covered rig's
    /// guiding gets stamped onto the other rig's frames. That failure is silent in every counter,
    /// which is why the count is taken here.
    /// </remarks>
    public static IReadOnlyList<string> NightRigs(GalactiLogContext context, DateOnly night)
        => context.Images
            .Where(i => i.SessionDate == night
                && i.ImageType == LightImageType
                && i.Telescope != null
                && i.Telescope != "")
            .Select(i => i.Telescope!)
            .Distinct()
            .ToList();

    /// <summary>
    /// The guiding sessions whose time range overlaps one night's exposures. Spec 7.6: the lookup
    /// is a <b>time overlap, not a date equality</b>, so a session whose own <c>session_date</c>
    /// lands a day off the frames it covers is still found.
    /// </summary>
    /// <param name="context">The pass's one connection.</param>
    /// <param name="fromUtc">The span start less the 24 hour lookback. It is the lower bound that
    /// keeps this an index range scan on <c>ix_phd2_sessions_started_at_utc</c> rather than a full
    /// scan of the session table.</param>
    /// <param name="toUtc">The night's last exposure end.</param>
    /// <param name="mustReachUtc">The night's first exposure start. A session is in when its
    /// <c>ended_at_utc</c> is null, meaning truncated and still running as far as the data says,
    /// or reaches this instant.</param>
    /// <remarks>
    /// A session with no resolved zone has a null <c>started_at_utc</c> (ruling F1) and therefore
    /// cannot satisfy either bound. It is skipped here by construction rather than by a check a
    /// caller has to remember, and <see cref="ProfilesWithNoZone"/> is what names it afterwards.
    /// </remarks>
    public static IReadOnlyList<Phd2SessionRow> SessionsOverlapping(
        GalactiLogContext context, DateTime fromUtc, DateTime toUtc, DateTime mustReachUtc)
        => context.Phd2Sessions
            .Where(s => s.StartedAtUtc != null
                && s.StartedAtUtc >= fromUtc
                && s.StartedAtUtc <= toUtc
                && (s.EndedAtUtc == null || s.EndedAtUtc >= mustReachUtc))
            .Select(s => new Phd2SessionRow(
                s.Id,
                s.Telescope,
                s.EquipmentProfile,
                s.PixelScaleArcsec,
                s.StartedAtUtc!.Value,
                s.EndedAtUtc,
                s.Events))
            .ToList();

    /// <summary>
    /// The stored guide frames of the named sessions, ordered by session and time offset, which is
    /// the order <c>ix_phd2_frames_session_time</c> serves.
    /// </summary>
    /// <remarks>
    /// Five columns for the sessions in play and no others. The whole <c>phd2_frames</c> table is
    /// roughly 390,000 rows for a six-month corpus and is never loaded.
    /// </remarks>
    public static IReadOnlyList<Phd2FrameRow> FramesForSessions(
        GalactiLogContext context, IReadOnlyCollection<Guid> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var rows = new List<Phd2FrameRow>();
        foreach (var batch in sessionIds.Chunk(SessionBatchSize))
        {
            rows.AddRange(context.Phd2Frames
                .Where(f => batch.Contains(f.SessionId))
                .OrderBy(f => f.SessionId)
                .ThenBy(f => f.TimeOffset)
                .Select(f => new Phd2FrameRow(
                    f.SessionId, f.TimeOffset, f.RaRaw, f.DecRaw, f.Dropped)));
        }

        return rows;
    }

    /// <summary>
    /// The distinct equipment profiles of the sessions that resolved to no zone, which is exactly
    /// the sessions whose <c>started_at_utc</c> is null (ruling F1), <b>restricted to the nights
    /// this pass visited</b>. A null entry is a section whose header named no profile; the caller
    /// labels it.
    /// </summary>
    /// <remarks>
    /// Spec 10.9's row for <c>phd2_timezone_unset</c> reads "at least one guiding session that met
    /// a frame this pass would have written", so a corpus-wide list is wrong twice over: it names
    /// profiles the pass never went near, and it names them again on every scan for as long as one
    /// frame anywhere stays below a coverage gate. That is how an activity feed stops being read.
    /// <para>
    /// An unzoned session has a null <c>started_at_utc</c> AND a null <c>session_date</c>, so it
    /// belongs to no night by either stored key. <c>started_at_local</c> is non-nullable and is
    /// populated for it, and no zone shifts a wall clock by more than about fourteen hours, so its
    /// local date within one day of a visited night is a sound "this session could be about that
    /// night" test and is the only one stored state supports.
    /// </para>
    /// <para>
    /// The date test runs in memory because <c>DateOnly.FromDateTime</c> has no SQLite translation.
    /// Only the unzoned rows are drawn down, which is the set the user is being warned about and is
    /// empty on a correctly configured library.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string?> ProfilesWithNoZone(
        GalactiLogContext context, IReadOnlyCollection<DateOnly> nights)
    {
        if (nights.Count == 0)
        {
            return [];
        }

        var wanted = new HashSet<DateOnly>();
        foreach (var night in nights)
        {
            wanted.UnionWith(NightDates(night));
        }

        return context.Phd2Sessions
            .Where(s => s.StartedAtUtc == null)
            .Select(s => new { s.EquipmentProfile, s.StartedAtLocal })
            .AsEnumerable()
            .Where(row => wanted.Contains(DateOnly.FromDateTime(row.StartedAtLocal)))
            .Select(row => row.EquipmentProfile)
            .Distinct()
            .ToList();
    }


    /// <summary>
    /// Spec 7.6's "The clear-side hold-out": true when the night has at least one unzoned candidate
    /// and no zoned session at all. Such a night is left out of the re-derive clear, because
    /// nothing records the zone a stored value was derived under and an empty setting now says
    /// nothing about a value written last week under a correct one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two date tests, both widened by one day either side, and no time overlap anywhere. An
    /// unzoned session has a null <c>started_at_utc</c> AND a null <c>session_date</c> (ruling F1),
    /// so it has no instant to overlap with and <c>started_at_local</c> is the only stored key it
    /// has; a zoned one would otherwise have to be matched against a span computed over the night's
    /// ALREADY FILLED frames, which is a second query and a second definition of "the night's
    /// guiding". The widening is sound for the same reason it is everywhere else in this file: no
    /// zone shifts a wall clock by more than about fourteen hours.
    /// </para>
    /// <para>
    /// The error it can make falls the safe way. The zoned test is deliberately wider than a true
    /// overlap, so a session found a day wide of its night makes the pass clear where the hold-out
    /// would have kept, which is the behaviour that shipped before the hold-out existed. A stale
    /// value surviving a correction is not reachable from it.
    /// </para>
    /// <para>
    /// The unzoned half is expressed as a half-open range on the stored <c>started_at_local</c>
    /// rather than as a date equality, because <c>DateOnly.FromDateTime</c> has no SQLite
    /// translation. The cheap half runs first and the second is not issued at all when it answers
    /// false, which is every night in a correctly configured library.
    /// </para>
    /// </remarks>
    public static bool IsUnzonedOnlyNight(GalactiLogContext context, DateOnly night)
    {
        var (from, to) = NightWindow(night);

        // The ZONED half runs first, and it is the INDEXED half (task2d-review.md P3-4).
        // phd2_sessions carries an index on session_date and none on started_at_local, so the
        // unzoned test below is a table scan per night it is issued for. Answering the indexed
        // question first means the scan is paid only on a night with no zoned session within a
        // day, which on a correctly configured library is no night at all. The selectivity
        // argument points the other way, at the unzoned rows being the rarer set, but selectivity
        // does not help a predicate the database can only answer by reading every row.
        var hasZonedSession = context.Phd2Sessions.Any(s => s.SessionDate != null
            && s.SessionDate >= from
            && s.SessionDate <= to);
        if (hasZonedSession)
        {
            return false;
        }

        var (fromLocal, toLocal) = NightWindowLocal(night);
        return context.Phd2Sessions.Any(s => s.StartedAtUtc == null
            && s.StartedAtLocal >= fromLocal
            && s.StartedAtLocal < toLocal);
    }

    /// <summary>
    /// Spec 7.6's one-day widening, and the ONE statement of it in this file. A guiding session's
    /// own date lands a day off the frames it covers whenever the longitude behind it is unset,
    /// and no zone shifts a wall clock by more than about fourteen hours, so one day either side
    /// is the sound bound and the same bound everywhere.
    /// </summary>
    /// <remarks>
    /// It was hand written five times, four building <c>DateOnly</c> sets and a fifth needing SQL
    /// bounds as well, with nothing to make them move together (task2d-review.md P3-1,
    /// phase-review.md F12). Design lesson 1. A source-text case pins the absence of a sixth.
    /// </remarks>
    private static (DateOnly From, DateOnly To) NightWindow(DateOnly night)
        => (night.AddDays(-1), night.AddDays(1));

    /// <summary>The same window as the three dates it covers, for the callers that build a
    /// <c>DateOnly</c> set.</summary>
    private static IEnumerable<DateOnly> NightDates(DateOnly night)
    {
        var (from, to) = NightWindow(night);
        return [from, night, to];
    }

    /// <summary>The same window as a half-open range of local wall-clock instants, which is the
    /// form a SQL-side test on <c>started_at_local</c> needs: <c>DateOnly.FromDateTime</c> has no
    /// SQLite translation, so the date equality is expressed as a range instead. The upper bound
    /// is midnight after the last day of the window and the comparison against it is strictly
    /// less than, so the whole of that last day is inside.</summary>
    private static (DateTime From, DateTime To) NightWindowLocal(DateOnly night)
    {
        var (from, to) = NightWindow(night);
        return (from.ToDateTime(TimeOnly.MinValue), to.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    private static IEnumerable<DateOnly> GuideNights(GalactiLogContext context)
        => context.Phd2Sessions
            .Where(s => s.SessionDate != null)
            .Select(s => s.SessionDate!.Value)
            .Distinct()
            .ToList();
}
