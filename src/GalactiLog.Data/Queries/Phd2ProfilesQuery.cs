using GalactiLog.Core.Io;
using GalactiLog.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>Spec 12.7's PHD2 profiles panel, one row per distinct <c>equipment_profile</c>, the
/// seven read-only facts the panel's table names.</summary>
/// <param name="Profile">The empty string for a session whose <c>equipment_profile</c> is null or
/// empty; the panel labels that row the way spec 10.9's warning labels it, "(no equipment
/// profile)".</param>
/// <param name="GuideCamera">The most recent session's <c>guide_camera</c>.</param>
/// <param name="FocalLengthMm">The most recent session's <c>focal_length_mm</c>.</param>
/// <param name="PixelScaleArcsec">The most recent session's <c>pixel_scale_arcsec</c>. Null is the
/// ASIAIR case of spec 7.6, and the panel renders "not in the log".</param>
/// <param name="SessionCount">The count of <c>phd2_sessions</c> rows carrying the profile.</param>
/// <param name="FirstSeen">The earliest of <c>started_at_utc</c>, falling back to
/// <c>started_at_local</c> per row when that row's zone never resolved.</param>
/// <param name="LastSeen">The latest of the same, and the key the rows are ordered by, newest
/// first.</param>
/// <param name="FirstSeenIsLogClock">Which of the two things <see cref="FirstSeen"/> actually is:
/// false for a resolved UTC instant, true for the guide log's own local wall clock, which belongs
/// to no zone and must never be converted as though it did. Carried per value rather than derived
/// by the reader from the zone that resolves NOW, because a panel that has just had a zone set on
/// it still holds the value this read gave it and the re-derive that makes it an instant is
/// asynchronous (fix-wave review P2-1).</param>
/// <param name="LastSeenIsLogClock">The same, for <see cref="LastSeen"/>. Reported separately
/// because the fallback is per session: a profile that mixes zoned and unzoned sessions can take
/// its earliest from one kind and its latest from the other.</param>
public sealed record Phd2ProfileRow(
    string Profile,
    string? GuideCamera,
    double? FocalLengthMm,
    double? PixelScaleArcsec,
    long SessionCount,
    DateTime FirstSeen,
    DateTime LastSeen,
    bool FirstSeenIsLogClock = false,
    bool LastSeenIsLogClock = false);

/// <summary>
/// The read behind spec 12.7's PHD2 profiles panel: the equipment profiles the guide logs
/// themselves named, not a list the panel or the user maintains.
/// </summary>
/// <remarks>
/// Read-only, on a connection closed with the read, the shape <see cref="DiagnosticsQuery"/>
/// already uses (spec 2.1.2, house rule 4.3). Every value the panel then edits, the telescope
/// mapping, the per-profile timezone and the per-profile site, lives in
/// <c>general.phd2_profile_map</c> and is read through <c>GalactiLog.Core.Phd2.Phd2Profiles</c>,
/// never here: this type answers only what the guide logs recorded.
/// </remarks>
public sealed class Phd2ProfilesQuery(DatabaseConnectionString connectionString)
{
    /// <summary>
    /// One row per distinct <c>equipment_profile</c>, ordered by <see cref="Phd2ProfileRow.LastSeen"/>
    /// descending. The four session-scoped facts (camera, focal length, pixel scale) are the
    /// profile's most recently seen session's, never the first, so a rig that changed guide
    /// cameras shows the current one.
    /// </summary>
    public IReadOnlyList<Phd2ProfileRow> Read()
    {
        using var context = OpenReadOnly();

        // Projected to the six columns the grouping below needs, so the in-memory pass carries
        // nothing else across from a corpus that can hold many sessions per profile. One round
        // trip, so there is no N plus 1 here; the cost does scale with sessions rather than with
        // profiles, and the grouping, the count, the min and the max are all expressible in SQL if
        // a corpus ever makes that worth doing (review P3-3).
        var sessions = context.Phd2Sessions
            .Select(session => new
            {
                session.EquipmentProfile,
                session.GuideCamera,
                session.FocalLengthMm,
                session.PixelScaleArcsec,
                session.StartedAtUtc,
                session.StartedAtLocal,
            })
            .ToList();

        var rows = new List<Phd2ProfileRow>();
        foreach (var group in sessions.GroupBy(session => session.EquipmentProfile ?? ""))
        {
            // The effective "seen" instant for a row is its own resolved UTC start, falling back
            // to the stored local wall clock when that row's zone never resolved (spec 7.6): the
            // fallback is per session, not per profile, so a profile mixing resolved and unset
            // rows still gets a real first and last seen instead of a null one.
            var seen = group
                .Select(session => (Session: session, Seen: session.StartedAtUtc ?? session.StartedAtLocal))
                .ToList();

            // The two extremes are taken as ENTRIES rather than as bare values, so each one's own
            // basis travels with it: the reader must not have to guess whether a given value was a
            // resolved instant or the log's wall clock.
            var latestEntry = seen.OrderByDescending(entry => entry.Seen).First();
            var earliestEntry = seen.OrderBy(entry => entry.Seen).First();
            var latest = latestEntry.Session;

            rows.Add(new Phd2ProfileRow(
                group.Key,
                latest.GuideCamera,
                latest.FocalLengthMm,
                latest.PixelScaleArcsec,
                group.LongCount(),
                earliestEntry.Seen,
                latestEntry.Seen,
                earliestEntry.Session.StartedAtUtc is null,
                latestEntry.Session.StartedAtUtc is null));
        }

        // Tie-broken on the profile name: two profiles last seen in the same instant would
        // otherwise order differently between runs, which reorders the panel under the user for no
        // reason they can see (review P3-4).
        return [.. rows
            .OrderByDescending(row => row.LastSeen)
            .ThenBy(row => row.Profile, StringComparer.Ordinal)];
    }

    // Read-only, unpooled, closed with the read: the same reason DiagnosticsQuery.OpenReadOnly
    // gives, and this panel's read is exactly as occasional as a Diagnostics refresh.
    private GalactiLogContext OpenReadOnly()
        => new(GalactiLogContextOptions.Create(DatabasePaths.AsReadOnly(connectionString.Value)));
}
