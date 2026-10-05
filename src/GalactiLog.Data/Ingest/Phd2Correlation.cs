using System.Linq.Expressions;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// What one correlation pass did. Every figure the <c>phd2_correlation_complete</c> and
/// <c>phd2_correlation_unattributed</c> details documents of spec 10.9 need, so the caller that
/// raises them recomputes nothing. The <c>trigger</c> key is the caller's own: this type does not
/// know whether it was run by a scan (<c>scan</c>) or by a settings save
/// (<c>settings_change</c>).
/// </summary>
/// <param name="Nights"><c>nights</c>: the imaging nights the pass visited, which is also the
/// <c>TotalSteps</c> of its progress envelope.</param>
/// <param name="FramesConsidered"><c>frames_considered</c>: unfilled LIGHT frames looked at across
/// those nights.</param>
/// <param name="Filled"><c>filled</c>: frames whose three arcsecond columns this pass wrote.
/// Counted from the rows the write statement actually touched, never from the rows the pass
/// intended to touch.</param>
/// <param name="Cleared"><c>cleared</c>: guiding values emptied by the re-derive, counted from the
/// rows the clear statement actually touched. That is every row on a visited night carrying a
/// non-null RMS that ruling F6 lets this pass write, which is the <c>phd2</c>-sourced rows and, by
/// spec 5.2's invariant that a non-null RMS always carries a source, nothing else: a <c>csv</c> row
/// is refused by <see cref="Phd2Correlation.NeverOverwritesCsv"/> and a value with a null source is
/// a shape the schema contract forbids. Zero in incremental mode, which re-derives nothing.</param>
/// <param name="BelowGate"><c>below_gate</c>: frames that had samples but failed the 10 sample or
/// 50 percent coverage gate, and therefore kept null columns.</param>
/// <param name="UnattributedProfiles"><c>profiles</c> of <c>phd2_correlation_unattributed</c>,
/// sorted and distinct across the pass.</param>
/// <param name="UnattributedNights"><c>nights</c> of that same event: how many visited nights
/// produced at least one unattributed profile.</param>
/// <param name="TimezoneUnsetProfiles">Profiles whose sessions resolved to no zone (ruling F1), for
/// the <c>phd2_timezone_unset</c> event whose type string and shape Task 4b owns. Empty when the
/// pass visited no night.</param>
/// <param name="PixelScaleMissingProfiles">Profiles whose sessions this pass would otherwise have
/// correlated but which carry no pixel scale, for <c>phd2_pixel_scale_missing</c>.</param>
/// <param name="Cancelled">True when the token stopped the pass part way. The figures are then
/// what was done, not what was planned.</param>
public sealed record Phd2CorrelationResult(
    int Nights,
    int FramesConsidered,
    int Filled,
    int Cleared,
    int BelowGate,
    IReadOnlyList<string> UnattributedProfiles,
    int UnattributedNights,
    IReadOnlyList<string> TimezoneUnsetProfiles,
    IReadOnlyList<string> PixelScaleMissingProfiles,
    bool Cancelled)
{
    /// <summary>A pass with nothing to visit: every counter zero and every list empty.</summary>
    public static readonly Phd2CorrelationResult Empty =
        new(0, 0, 0, 0, 0, [], 0, [], [], false);
}

/// <summary>
/// Spec 7.6's Correlation subsection: fills a LIGHT frame's three guiding columns from the stored
/// guide frames of the rig that was guiding it and stamps <c>guiding_rms_source</c> <c>phd2</c>.
/// Port of <c>backend/app/services/phd2_correlation.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writer census (TRACKING section 6 item 14).</b> This type is the <b>one</b> new writer of
/// the <c>images</c> guiding columns beside the CSV backfill of spec 7.4, and
/// <see cref="WriteGuiding"/> is the one method in the solution that writes a <c>phd2</c> source.
/// A second write path anywhere is the failure ruling F6 exists to refuse.
/// </para>
/// <para>
/// THIS TYPE PERFORMS NO FILESYSTEM ACCESS AT ALL. It names no <c>System.IO</c> member, opens,
/// creates, writes, moves or deletes no file, and needs no <c>FileSafetyTest</c> allowlist entry.
/// Everything it changes is catalogue rows.
/// </para>
/// <para>
/// It opens one connection and hands that one context to every read (spec 5.1's single-writer
/// contract). SQLite under that contract has no concurrent second correlation pass to serialise,
/// which is why the port takes no advisory lock where the web takes
/// <c>pg_advisory_xact_lock</c>.
/// </para>
/// </remarks>
public static class Phd2Correlation
{
    /// <summary>The one value this pass writes into <c>images.guiding_rms_source</c>.</summary>
    public const string Phd2Source = "phd2";

    /// <summary>
    /// Spec 7.6's per-frame coverage gate, part one: at least this many samples must fall inside
    /// the exposure window. <c>Phd2Metrics.MinFrames</c> of 100 is a <b>session</b> gate and does
    /// not apply here: an exposure window is seconds to minutes long, so a hundred samples would
    /// exclude every sub shorter than about a minute at a half-second guide exposure.
    /// </summary>
    public const int MinSamples = 10;

    /// <summary>
    /// Spec 7.6's per-frame coverage gate, part two: the samples must span at least this fraction
    /// of the exposure. Twenty samples from the first ten seconds of a five-minute sub say nothing
    /// about the other 290, and a confident wrong number is worse than an honest blank.
    /// </summary>
    public const double MinCoverage = 0.5;

    /// <summary>
    /// Spec 7.6: how far before a night's first exposure to look for a guiding session that could
    /// already be running when it starts. PHD2 sessions run for hours.
    /// </summary>
    public static readonly TimeSpan SessionLookback = TimeSpan.FromHours(24);

    /// <summary>
    /// What a section carrying no equipment profile is called in a warning's profile list. Both
    /// spellings of "no profile" fold onto it, which is why the test is
    /// <see cref="string.IsNullOrEmpty(string?)"/>: an ASIAIR header never produces the empty
    /// string, but a stored column filled with <c>?? ""</c> can. The same literal is
    /// <c>Phd2Ingest.NoProfileLabel</c>, which is private on a type this pass does not otherwise
    /// touch; if it is ever made reachable this declaration goes away and a reference takes its
    /// place.
    /// </summary>
    private const string NoProfileLabel = "(no equipment profile)";

    /// <summary>
    /// <b>Ruling F6, declared once and applied by nothing else.</b> Spec 7.6 states the guarantee
    /// as a block quote: a value whose <c>guiding_rms_source</c> is <c>csv</c> is never
    /// overwritten, never cleared and never read as a candidate for filling, by any path in this
    /// section.
    /// </summary>
    /// <remarks>
    /// It is not a check each caller remembers. It sits inside the <c>ExecuteUpdate</c> statement
    /// of <see cref="WriteGuiding"/>, so no caller can defeat it by forgetting one, and it is the
    /// same expression <c>Phd2Queries</c> filters its fill candidates by, so the read half of the
    /// guarantee cannot drift from the write half either. Design lesson 2 applied to data
    /// integrity: the guarantee lives at the choke point.
    /// </remarks>
    internal static readonly Expression<Func<Image, bool>> NeverOverwritesCsv =
        image => image.GuidingRmsSource == null || image.GuidingRmsSource == Phd2Source;

    /// <summary>
    /// Runs one correlation pass. The single entry point: a scan calls it with the nights the
    /// guide-log ingest just touched, a settings change calls it with
    /// <see cref="InvalidatedNights"/>, and a pass with no night set runs the incremental mode.
    /// </summary>
    /// <param name="connectionString">The library database. One connection is opened and every
    /// read shares it, as <see cref="Phd2Ingest"/> and <see cref="OrphanPruner"/> take theirs.
    /// </param>
    /// <param name="nights">The imaging nights to re-derive: every <c>phd2</c>-sourced value on
    /// them is cleared before the refill, because a re-ingested log or an edited map can
    /// invalidate a value that was correct when it was written. <b>Null is the incremental
    /// mode</b>: visit only the nights holding a LIGHT frame with no guiding RMS at all, fill what
    /// qualifies, re-derive nothing. An empty set is not null: it visits nothing at all.</param>
    /// <param name="profileMap">The normalised profile map of <c>general.phd2_profile_map</c>,
    /// through <c>Phd2Profiles.Normalize</c>. It is what makes a map change worth re-running: see
    /// <see cref="Phd2Profiles.EffectiveTelescope"/>.</param>
    /// <param name="aliasMap">The telescope alias map of spec 9.1, normally
    /// <c>AliasMapCache.Current</c>. Both sides of a rig name comparison are user data written at
    /// different times, so a raw string comparison here is a defect.</param>
    /// <param name="report">(step, totalSteps, message, force) for the <c>phd2_correlate</c>
    /// envelope of spec 10.4, in the shape <see cref="Phd2Ingest"/> reports its own. The fourth
    /// argument is the coordinator's force flag, which is why the first envelope and the last one
    /// both send it true.</param>
    /// <param name="ct">A cancelled pass stops between nights and returns what it had done. It
    /// never leaves a night half cleared: the clear and the refill of one night are consecutive.
    /// </param>
    public static Phd2CorrelationResult Run(
        string connectionString,
        IReadOnlyCollection<DateOnly>? nights,
        IReadOnlyDictionary<string, Phd2ProfileEntry> profileMap,
        AliasMap aliasMap,
        Action<int, int, string, bool>? report,
        CancellationToken ct)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString, tracking: false));

        var reDerive = nights is not null;
        var targets = reDerive
            ? nights!.Distinct().Order().ToList()
            : Phd2Queries.NightsNeedingFill(context).ToList();

        if (targets.Count == 0)
        {
            report?.Invoke(0, 0, "No guiding to correlate.", true);
            return Phd2CorrelationResult.Empty;
        }

        report?.Invoke(0, targets.Count, "Correlating PHD2 guiding...", true);

        var unattributed = new SortedSet<string>(StringComparer.Ordinal);
        var noScale = new SortedSet<string>(StringComparer.Ordinal);
        var unattributedNights = 0;
        var considered = 0;
        var filled = 0;
        var cleared = 0;
        var belowGate = 0;
        var visitedNights = new List<DateOnly>();
        var cancelled = false;

        foreach (var night in targets)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            // One night's clear and its refill commit together, so a crash between them cannot
            // leave the night emptied (review P3-5). The token is checked above the transaction,
            // so cooperative cancellation never splits them either.
            using var transaction = context.Database.BeginTransaction();

            if (reDerive && !Phd2Queries.IsUnzonedOnlyNight(context, night))
            {
                // Scoped to the night's frames that carry a value at all, so a frame that had
                // nothing is not counted as cleared. The csv half is deliberately NOT repeated
                // here: it is WriteGuiding's own predicate, which is the whole of ruling F6. Take
                // that predicate away and this statement empties a CSV measurement, which is
                // exactly what the never-overwrite cases catch.
                //
                // Spec 7.6's clear-side hold-out is the other half of the condition: a night whose
                // only guiding is unzoned keeps what it has, because nothing records the zone a
                // stored value was derived under. The night is still refilled below, and fills
                // nothing, so the hold-out adds no second place where the fill rule is decided.
                cleared += WriteGuiding(
                    context,
                    image => image.SessionDate == night && image.GuidingRmsArcsec != null,
                    null, null, null, null);
            }

            var outcome = CorrelateOneNight(context, night, profileMap, aliasMap, noScale);
            considered += outcome.Considered;
            filled += outcome.Filled;
            belowGate += outcome.BelowGate;
            if (outcome.Unattributed.Count > 0)
            {
                unattributedNights++;
                unattributed.UnionWith(outcome.Unattributed);
            }

            transaction.Commit();

            visitedNights.Add(night);
            report?.Invoke(visitedNights.Count, targets.Count, $"Correlated {filled} frame(s)...", false);
        }

        // Spec 10.9: named only for the nights this pass visited, so a profile the pass never went
        // near is not warned about, and warned about once per pass rather than once per session.
        // Ruling F1: the zone decision is already stored as a null started_at_utc, and nothing here
        // resolves a zone or reads the machine's.
        var unzoned = Phd2Queries.ProfilesWithNoZone(context, visitedNights)
            .Select(Label)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var result = new Phd2CorrelationResult(
            visitedNights.Count, considered, filled, cleared, belowGate,
            [.. unattributed], unattributedNights, unzoned, [.. noScale], cancelled);

        // A forced closing envelope with the final figures, the way Phd2Ingest closes its own: the
        // in-loop envelopes are unforced and the status bar may coalesce the last one away, leaving
        // a stale count on screen (review P3-3).
        report?.Invoke(
            result.Nights, targets.Count, $"Correlated {result.Filled} frame(s).", true);
        return result;
    }

    /// <summary>
    /// Spec 7.6's invalidation set for a settings change, read on its own short-lived connection so
    /// a caller that holds no database handle (Task 6's runner) can ask for it.
    /// </summary>
    /// <remarks>
    /// Call it <b>after</b> <see cref="RederiveSessionTimes"/> and never before. A session that has
    /// just gained a zone gains a non-null <c>session_date</c> in the same pass, and the second
    /// half of this union is "every night holding a guiding session", so the nights that only
    /// became reachable through the re-derive are already covered. Read in the other order, they
    /// are not.
    /// </remarks>
    public static IReadOnlyList<DateOnly> InvalidatedNights(string connectionString)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString));
        return Phd2Queries.InvalidatedNights(context);
    }

    /// <summary>
    /// The set <see cref="Run"/>'s incremental mode visits: the nights holding a LIGHT frame with
    /// no guiding RMS at all, read on its own short-lived connection so a caller can union it into
    /// an explicit night set instead of making a second pass for it.
    /// </summary>
    /// <remarks>
    /// It exists so one pass can cover both halves of what a scan owes. A scan's explicit set is
    /// the nights it ingested, and <c>ScanWriter</c> nulls a <c>phd2</c>-sourced value on any image
    /// row it rewrites, whichever night that row belongs to. Running the pass twice would visit a
    /// night in both sets twice and double its figures, so the caller unions the two sets and calls
    /// <see cref="Run"/> once. No query logic lives here: this is
    /// <c>Phd2Queries.NightsNeedingFill</c> on a connection of its own, which is the same read the
    /// incremental mode makes on the pass's own context.
    /// </remarks>
    public static IReadOnlyList<DateOnly> NightsNeedingFill(string connectionString)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString));
        return Phd2Queries.NightsNeedingFill(context);
    }

    /// <summary>
    /// Spec 7.6's "Re-deriving session times": recomputes <c>started_at_utc</c>, <c>ended_at_utc</c>,
    /// <c>duration_s</c> and <c>session_date</c> on every <c>phd2_sessions</c> row, and
    /// <c>started_at_utc</c> and <c>session_date</c> on every <c>phd2_calibrations</c> row, from the
    /// stored naive <c>started_at_local</c> and <c>ended_at_local</c> under the CURRENT resolution
    /// order of ruling F1. Returns how many rows changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This member writes <c>phd2_sessions</c> and <c>phd2_calibrations</c>, the catalogue's own
    /// tables. It never writes <c>images</c>, never a guiding column, and never a user file.</b>
    /// The <c>images</c> guiding columns have exactly one writer here, <see cref="WriteGuiding"/>,
    /// and this method does not call it.
    /// </para>
    /// <para>
    /// It is the whole mechanism behind "set the zone later, then save". A log whose file has not
    /// changed is never re-read and its <c>phd2_logs</c> row is skipped by the delta test of spec
    /// 10.3, so without this a session ingested while its profile had no zone would keep its null
    /// <c>started_at_utc</c> forever and the correlation would still have nothing to match.
    /// </para>
    /// <para>
    /// <c>started_at_local</c> and <c>ended_at_local</c> are <b>read and never written</b>, never given a
    /// <see cref="DateTimeKind"/> and never converted in place. Every derived value is computed
    /// through the same public members the ingest calls (<c>Phd2Profiles.ZoneResolver</c>,
    /// <c>Phd2Profiles.LongitudeResolver</c>, <see cref="Phd2Metrics.LocalToUtc"/> and
    /// <c>Sessions.SessionDate.Compute</c>), so an ingest and a re-derive of the same row cannot
    /// disagree. No machine zone is read: no <c>TimeZoneInfo.Local</c>, no <c>DateTime.Now</c>, no
    /// <c>ToLocalTime</c>.
    /// </para>
    /// <para>
    /// A row is written only where a derived value differs from the stored one, including a
    /// difference to null when a configured zone is removed. Change tracking is what decides that:
    /// assigning an identical value marks nothing modified, so <c>SaveChanges</c> writes no row and
    /// returns 0.
    /// </para>
    /// <para>
    /// ponytail: the session and calibration tables are walked whole as tracked entities, which is
    /// the pattern the web's own <c>apply_profile_map</c> uses and which its comment calls
    /// affordable over 800 session rows. It is not affordable over <c>phd2_frames</c> and is never
    /// pointed at it. Upgrade path if a corpus ever makes it measurable: read the four columns as a
    /// projection and issue one <c>ExecuteUpdate</c> per changed row.
    /// </para>
    /// </remarks>
    /// <param name="connectionString">The library database.</param>
    /// <param name="general">The settings snapshot, which carries the raw
    /// <c>phd2_profile_map</c>, <c>observer_timezone</c>, <c>observer_longitude</c> and
    /// <c>use_imaging_night</c>. It is taken whole rather than as a normalised map because
    /// <c>Phd2Profiles.ZoneResolver</c> and <c>LongitudeResolver</c> take the raw
    /// <see cref="System.Text.Json.JsonElement"/>, and it is the same argument
    /// <c>Phd2Ingest.Run</c> takes, which is what keeps the two from drifting.</param>
    /// <param name="ct">A cancelled re-derive stops before writing anything.</param>
    public static int RederiveSessionTimes(
        string connectionString, GeneralSettings general, CancellationToken ct)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString, tracking: true));

        var resolveZone = Phd2Profiles.ZoneResolver(general.Phd2ProfileMap, general.ObserverTimezone);
        var loadZone = Phd2Profiles.ZoneLoader();
        // One loader shared between the call site and step 3 (spec 8.3), so a re-derive over a
        // whole catalogue resolves each zone once rather than once per row. ZoneResolver builds
        // its own internally, so this is not the only loader the pass holds.
        var resolveLongitude = Phd2Profiles.LongitudeResolver(
            general.Phd2ProfileMap, general.ObserverLongitude, general.ObserverTimezone, loadZone);

        foreach (var row in context.Phd2Sessions)
        {
            if (ct.IsCancellationRequested)
            {
                return 0;
            }

            var zone = loadZone(resolveZone(row.EquipmentProfile).Zone);
            var startedUtc = Phd2Metrics.LocalToUtc(row.StartedAtLocal, zone);

            // Spec 7.6: the end is converted from the stored wall clock through the same member the
            // start goes through, so an ingest and a re-derive of the same row cannot disagree.
            // That is the property that matters, and the reconstruction this replaces could not
            // have it: an unzoned session's duration_s is a difference of two wall clocks, so the
            // re-derived start plus those elapsed seconds is an hour out across a transition.
            // A row with no stored local end has no end. That is the truncated section, which spec
            // 5.16 has always given a null ended_at_utc, and no end is invented for it.
            var endedUtc = row.EndedAtLocal is { } endedLocal
                ? Phd2Metrics.LocalToUtc(endedLocal, zone)
                : null;

            // Re-derived from the pair whenever both are known. Where they are not, which is a
            // truncated section or a zone that still does not resolve, the stored value stands: it
            // came from the local difference or from the last frame's time offset and there is
            // nothing better to offer. Rounded as Phd2Metrics.ComputeSessionMetrics rounds it, or
            // every pass would rewrite a row the ingest had already written correctly.
            if (startedUtc is { } start && endedUtc is { } end)
            {
                row.DurationS = PythonNumerics.RoundLikePython((end - start).TotalSeconds, 3);
            }

            row.StartedAtUtc = startedUtc;
            row.EndedAtUtc = endedUtc;
            row.SessionDate = GalactiLog.Core.Sessions.SessionDate.Compute(
                startedUtc, general.UseImagingNight, resolveLongitude(row.EquipmentProfile, startedUtc));
        }

        // Spec 5.18 gives a calibration two derived time columns and no end of any kind, so the
        // same rule runs over two columns here rather than three. Nothing is invented for the
        // missing one.
        foreach (var row in context.Phd2Calibrations)
        {
            if (ct.IsCancellationRequested)
            {
                return 0;
            }

            var startedUtc = Phd2Metrics.LocalToUtc(
                row.StartedAtLocal, loadZone(resolveZone(row.EquipmentProfile).Zone));
            row.StartedAtUtc = startedUtc;
            row.SessionDate = GalactiLog.Core.Sessions.SessionDate.Compute(
                startedUtc, general.UseImagingNight, resolveLongitude(row.EquipmentProfile, startedUtc));
        }

        return context.SaveChanges();
    }

    /// <summary>
    /// <b>The one method in this solution that writes any of the four <c>images</c> guiding
    /// columns from a guide log, and the one that writes the string <c>phd2</c> into
    /// <c>guiding_rms_source</c>.</b> The fill passes the three arcsecond values and
    /// <see cref="Phd2Source"/>; the clear passes four nulls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NeverOverwritesCsv"/> is applied here, inside the update statement, on every
    /// call and unconditionally. A caller chooses the scope and nothing else: it cannot widen the
    /// statement onto a <c>csv</c> row by forgetting a check, which is the whole of ruling F6 and
    /// of design lesson 2.
    /// </para>
    /// <para>
    /// ponytail: one <c>ExecuteUpdate</c> per filled frame, where the web issues one
    /// <c>bulk_update_mappings</c> per night (questions-b Q8, accepted as proposed). That is the
    /// price of putting the predicate in the statement rather than in a caller, and it is paid on
    /// a single-writer SQLite database whose whole pass is bounded by the guide-log corpus.
    /// Upgrade path, if a night of thousands of frames ever measures slow: one statement per
    /// distinct value triple, keyed by value group, carrying this same <c>Where</c>.
    /// </para>
    /// </remarks>
    private static int WriteGuiding(
        GalactiLogContext context,
        Expression<Func<Image, bool>> scope,
        double? raArcsec,
        double? decArcsec,
        double? totalArcsec,
        string? source)
        => context.Images
            .Where(scope)
            .Where(NeverOverwritesCsv)
            .ExecuteUpdate(set => set
                .SetProperty(image => image.GuidingRmsRaArcsec, raArcsec)
                .SetProperty(image => image.GuidingRmsDecArcsec, decArcsec)
                .SetProperty(image => image.GuidingRmsArcsec, totalArcsec)
                .SetProperty(image => image.GuidingRmsSource, source));

    private readonly record struct NightOutcome(
        int Considered, int Filled, int BelowGate, IReadOnlyList<string> Unattributed)
    {
        public static readonly NightOutcome Nothing = new(0, 0, 0, []);
    }

    private static NightOutcome CorrelateOneNight(
        GalactiLogContext context,
        DateOnly night,
        IReadOnlyDictionary<string, Phd2ProfileEntry> profileMap,
        AliasMap aliasMap,
        SortedSet<string> noScale)
    {
        var candidates = Phd2Queries.UnfilledLights(context, night);
        if (candidates.Count == 0)
        {
            return NightOutcome.Nothing;
        }

        var spanStart = candidates.Min(c => c.CaptureUtc);
        var spanEnd = candidates.Max(c => c.CaptureUtc.AddSeconds(c.ExposureSeconds));
        var sessions = Phd2Queries.SessionsOverlapping(
            context, spanStart - SessionLookback, spanEnd, spanStart);
        if (sessions.Count == 0)
        {
            return new NightOutcome(candidates.Count, 0, 0, []);
        }

        // Phd2Profiles.EffectiveTelescope is the one implementation of "which rig is this session
        // really", shared with GuidingStatsQuery and Phd2NightQuery (task5b-review.md P2-1). The
        // live map is its sole authority, so the stored phd2_sessions.telescope column is never
        // read here and a profile the reader has unmapped resolves to no rig, which is what lets
        // an unmapping take effect at all (phase-review.md F3).
        var rigOf = sessions.ToDictionary(
            s => s.Id,
            s => Phd2Profiles.EffectiveTelescope(s.EquipmentProfile, profileMap));

        // The FULL rig set, not the unfilled subset. See Phd2Queries.NightRigs for the silent
        // failure this prevents.
        var nightRigs = Phd2Queries.NightRigs(context, night);
        var canonicalRigs = new HashSet<string>(
            nightRigs.Select(rig => aliasMap.CanonicalTelescope(rig) ?? rig),
            StringComparer.OrdinalIgnoreCase);
        var multiRig = canonicalRigs.Count > 1;

        var attributed = new Dictionary<string, IReadOnlyList<Phd2SessionRow>>(StringComparer.Ordinal);
        foreach (var rig in nightRigs)
        {
            // Spec 7.6 step 1: the rig name is expanded to its canonical form plus every alias
            // before comparing, because one physical rig appears under several spellings in one
            // night's headers and the profile map may have been written under another one.
            // AliasMap.TelescopeMatchSet is the one implementation, shared with Phd2NightQuery
            // (phase-review.md F7).
            var selected = Phd2Metrics.SelectNightRows(
                sessions,
                s => rigOf[s.Id],
                s => s.EquipmentProfile,
                aliasMap.TelescopeMatchSet(rig));

            // Spec 7.6 step 3's veto: on a night whose frames span more than one rig, the
            // sole-unmapped-profile fallback of step 2 is vetoed outright. Guessing which rig a
            // target used would attach wrong guiding numbers to real data, which is worse than
            // showing none.
            if (selected.Count > 0
                && multiRig
                && !selected.Any(s => !string.IsNullOrEmpty(rigOf[s.Id])))
            {
                selected = [];
            }

            if (selected.Count > 0)
            {
                attributed[rig] = selected;
            }
        }

        var used = attributed.Values.SelectMany(rows => rows).Select(s => s.Id).ToHashSet();
        var unattributed = sessions
            .Where(s => !used.Contains(s.Id)
                && string.IsNullOrEmpty(rigOf[s.Id])
                && !string.IsNullOrEmpty(s.EquipmentProfile))
            .Select(s => s.EquipmentProfile!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (attributed.Count == 0)
        {
            return new NightOutcome(candidates.Count, 0, 0, unattributed);
        }

        // Frames are loaded only for rigs that have something to fill. nightRigs deliberately
        // includes rigs whose frames are all filled already, because the veto is counted over
        // them, and reducing their sample streams would be work with no possible output.
        var fillRigs = candidates
            .Where(c => !string.IsNullOrEmpty(c.Telescope))
            .Select(c => c.Telescope!)
            .ToHashSet(StringComparer.Ordinal);
        var relevant = new Dictionary<string, List<Phd2SessionRow>>(StringComparer.Ordinal);
        foreach (var (rig, rows) in attributed)
        {
            if (!fillRigs.Contains(rig))
            {
                continue;
            }

            var keep = new List<Phd2SessionRow>();
            foreach (var row in rows)
            {
                // Spec 7.6: a session with no pixel scale is never correlated. Its frames are
                // stored in pixels precisely so a wrong scale cannot be baked into them, and with
                // no scale there is nothing to convert them with. Named here rather than pass wide
                // so a profile is only reported once it has actually met a frame this pass would
                // have written.
                if (row.PixelScaleArcsec is not > 0)
                {
                    noScale.Add(Label(row.EquipmentProfile));
                    continue;
                }

                keep.Add(row);
            }

            if (keep.Count > 0)
            {
                relevant[rig] = keep;
            }
        }

        if (relevant.Count == 0)
        {
            return new NightOutcome(candidates.Count, 0, 0, unattributed);
        }

        var needed = relevant.Values.SelectMany(rows => rows).Select(s => s.Id).Distinct().ToList();
        var framesBySession = Phd2Queries.FramesForSessions(context, needed)
            .GroupBy(f => f.SessionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Phd2FrameRow>)g.ToList());

        // Sessions of one rig are pooled into one time-ordered stream, because PHD2 restarting
        // mid-exposure produces two sessions covering one frame and both describe how it was
        // guided. Sessions of a different rig are never pooled: their samples describe a different
        // optical train.
        var samplesByRig = relevant.ToDictionary(
            entry => entry.Key,
            entry => Pool(entry.Value, framesBySession),
            StringComparer.Ordinal);

        var filled = 0;
        var belowGate = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Telescope is null
                || !samplesByRig.TryGetValue(candidate.Telescope, out var samples)
                || samples.Length == 0)
            {
                continue;
            }

            if (WindowRms(samples, candidate.CaptureUtc, candidate.ExposureSeconds) is not { } rms)
            {
                belowGate++;
                continue;
            }

            filled += WriteGuiding(
                context,
                image => image.Id == candidate.Id,
                rms.Ra, rms.Dec, rms.Total, Phd2Source);
        }

        return new NightOutcome(candidates.Count, filled, belowGate, unattributed);
    }

    /// <summary>One absolute-time sample in arcseconds. Time is seconds since the Unix epoch, so a
    /// frame's window is a pair of comparisons rather than a pair of <c>DateTime</c>
    /// constructions.</summary>
    private readonly record struct Sample(double Time, double Ra, double Dec);

    private static Sample[] Pool(
        IReadOnlyList<Phd2SessionRow> rows,
        IReadOnlyDictionary<Guid, IReadOnlyList<Phd2FrameRow>> framesBySession)
    {
        var pooled = new List<Sample>();
        foreach (var row in rows)
        {
            if (row.PixelScaleArcsec is not { } scale
                || !framesBySession.TryGetValue(row.Id, out var frames)
                || frames.Count == 0)
            {
                continue;
            }

            // The window rule has ONE implementation, Phd2Metrics.DitherSettleWindows in its
            // (Type, TimeOffset) overload, fed from the events JSON read back out of
            // phd2_sessions. Nothing here derives a window: two implementations would eventually
            // disagree, at which point a per-frame figure and the session figure printed beside it
            // would stop describing the same frames.
            var windows = Phd2Metrics.DitherSettleWindows(
                StoredEvents(row.Events), frames[^1].TimeOffset);
            var baseSeconds = (row.StartedAtUtc - DateTime.UnixEpoch).TotalSeconds;

            foreach (var frame in frames)
            {
                if (frame.Dropped || frame.RaRaw is not { } ra || frame.DecRaw is not { } dec)
                {
                    continue;
                }

                if (Phd2Metrics.InWindows(frame.TimeOffset, windows))
                {
                    continue;
                }

                pooled.Add(new Sample(
                    baseSeconds + frame.TimeOffset, ra * scale, dec * scale));
            }
        }

        pooled.Sort((a, b) => a.Time.CompareTo(b.Time));
        return [.. pooled];
    }

    /// <summary>
    /// The pooled population standard deviations over the samples falling in
    /// <c>[capture, capture + exposure]</c>, rounded to six decimals, or null when either coverage
    /// gate fails. Null means "leave the columns null", which is the honest answer for a short sub
    /// with sparse guiding and warrants no warning.
    /// </summary>
    private static (double Ra, double Dec, double Total)? WindowRms(
        Sample[] samples, DateTime captureUtc, double exposureSeconds)
    {
        if (samples.Length == 0 || exposureSeconds <= 0)
        {
            return null;
        }

        var start = (captureUtc - DateTime.UnixEpoch).TotalSeconds;
        var end = start + exposureSeconds;

        var ra = new List<double>();
        var dec = new List<double>();
        var first = 0.0;
        var last = 0.0;
        for (var i = LowerBound(samples, start); i < samples.Length && samples[i].Time <= end; i++)
        {
            if (ra.Count == 0)
            {
                first = samples[i].Time;
            }

            last = samples[i].Time;
            ra.Add(samples[i].Ra);
            dec.Add(samples[i].Dec);
        }

        if (ra.Count < MinSamples || (last - first) / exposureSeconds < MinCoverage)
        {
            return null;
        }

        // The gate above guarantees both lists are non-empty, so the null branch of
        // PopulationSigma is unreachable here; it is pattern-matched rather than asserted away so
        // the member's own contract is what this depends on.
        if (Phd2Metrics.PopulationSigma(ra) is not { } rmsRa
            || Phd2Metrics.PopulationSigma(dec) is not { } rmsDec)
        {
            return null;
        }

        // The hypotenuse is taken over the unrounded axes and rounded once, as the session-level
        // figure is, so a frame's total and a session's total are computed the same way.
        // PythonNumerics.RoundLikePython, not Math.Round: phd2_correlation.py rounds these three the
        // same way phd2_metrics.py rounds the session figures, and one rounding rule with two
        // implementations is the same defect measured against the real corpus in Phase 15A.
        return (PythonNumerics.RoundLikePython(rmsRa, 6),
            PythonNumerics.RoundLikePython(rmsDec, 6),
            PythonNumerics.RoundLikePython(double.Hypot(rmsRa, rmsDec), 6));
    }

    // First index whose time reaches `time`, over an array already sorted by time. The Python uses
    // bisect for the same reason: a night is thousands of frames against tens of thousands of
    // samples, and a linear scan per frame is that product.
    private static int LowerBound(Sample[] samples, double time)
    {
        var lo = 0;
        var hi = samples.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (samples[mid].Time < time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // The stored events array, as (type, time offset) pairs. Reading the document is not window
    // arithmetic: every window decision below is Phd2Metrics'. Reading the document is not this
    // file's job either, and used to be: a second, lenient JsonDocument walk lived here and
    // skipped a malformed entry where the query side's strict reader returned nothing, which is
    // two readers of one document (task2-review.md P3-7). Phd2Events.Read is the survivor; this
    // projects its answer down to the two fields the window rule uses.
    private static IEnumerable<(string Type, double TimeOffset)> StoredEvents(string json)
        => Phd2Events.Read(json).Select(e => (e.Type, e.TimeOffset));

    private static string Label(string? profile)
        => string.IsNullOrEmpty(profile) ? NoProfileLabel : profile;
}
