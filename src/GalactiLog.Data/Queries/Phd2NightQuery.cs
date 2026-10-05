using System.Text.Json;
using GalactiLog.Core.Phd2;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// One night's guiding sessions and the rollup over exactly those sessions (spec 12.4), port of
/// <c>GET /phd2/sessions</c> (<c>api/phd2.py:42-107</c>) and of the session-detail night summary
/// (<c>services/target_detail.py:565-571</c>), answered together because one screen shows both.
/// </summary>
/// <remarks>
/// <para>
/// One statement over <c>phd2_sessions</c>, served by <c>ix_phd2_sessions_session_date</c>, on a
/// short-lived <see cref="GalactiLogContext"/> opened through
/// <see cref="GalactiLogContextOptions.Create(string, bool)"/> so
/// <c>PragmaConnectionInterceptor</c> stays in the path. <c>phd2_frames</c> is never read here:
/// the band shows stored aggregates and the graph asks for frames separately, only once it is
/// opened.
/// </para>
/// <para>
/// <b>This query owns no arithmetic.</b> The rig rule is
/// <see cref="Phd2Metrics.SelectNightRows{T}"/> and the rollup is
/// <see cref="Phd2Metrics.AggregateNight"/>, both called here and neither reimplemented, in SQL or
/// in C#. There is one weighted RMS, one hundred-frame gate and one median of medians in this
/// solution, so the Statistics page and a session card can never disagree about what a night's RMS
/// was. A local helper added here because a projection felt awkward would pass every figure case
/// on the day it was written and drift the first time the Core one was tuned; a source-text case
/// pins the absence.
/// </para>
/// <para>
/// <b>There is no multi-rig veto here.</b> Spec 7.6's veto is counted over the night's full
/// <c>images</c> rig set and belongs to the correlation alone. This query answers which guiding
/// sessions a rig's card is about, which the web answers the same way. A veto added here would
/// blank the Guiding band on every two-rig night whose profiles are not yet mapped, which is the
/// state every install starts in.
/// </para>
/// </remarks>
/// <param name="profileMap">The raw <c>general.phd2_profile_map</c>, normally
/// <c>() =&gt; settingsStore.GetGeneral().Phd2ProfileMap</c>, read once per <see cref="Get"/> and
/// never per row. Not optional and with no default: see
/// <see cref="Phd2Profiles.EffectiveTelescope"/> for what a reader that trusted the stored column
/// shows a user who has just mapped a profile (task5b-review.md P2-1).</param>
public sealed class Phd2NightQuery(
    DatabaseConnectionString connectionString,
    AliasMapCache aliases,
    Func<JsonElement?> profileMap)
{
    private readonly object _guideLogGate = new();
    private bool? _anyGuideLogs;

    /// <summary>The night's guiding, narrowed to one rig when <paramref name="telescope"/> names
    /// one. Never null: a night with no guiding session returns an empty session list and the
    /// empty rollup.</summary>
    /// <param name="night">The imaging night, a <c>phd2_sessions.session_date</c>. Ruling F1: a
    /// session with no resolved zone has a null <c>session_date</c>, so it is unreachable through
    /// this query and needs no warning here. <c>Phd2Correlation</c> already raises
    /// <c>phd2_timezone_unset</c> for it.</param>
    /// <param name="telescope">Null or empty means the whole night, unfiltered
    /// (<c>api/phd2.py:76</c>).</param>
    public Phd2NightGuiding Get(DateOnly night, string? telescope = null)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));

        // Read and normalised ONCE per call, never per row, and live rather than cached: nothing
        // writes the map's newer answer back to phd2_sessions.telescope, so the band would label
        // its rigs and pick its sessions from the state before the user's last remap.
        var profiles = Phd2Profiles.Normalize(profileMap());

        IReadOnlyList<Phd2SessionSummary> rows = context.Phd2Sessions
            .Where(s => s.SessionDate == night)
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => new Phd2SessionSummary(
                s.Id,
                // Safe by ruling F1: session_date and started_at_utc are null together, and this
                // statement already selected on session_date. Phd2Queries.SessionsOverlapping
                // reads the column the same way and for the same reason.
                s.StartedAtUtc!.Value,
                s.EndedAtUtc,
                s.DurationS,
                s.FrameCount,
                // api/phd2.py:87, row.equipment_profile or "". A null profile and an empty profile
                // are the same profile: the PHD2 embedded in an ASIAIR writes the line with
                // nothing but a trailing space, so one file gives the empty string where another
                // gives no line at all, and the rig rule's sole-profile branch has to see one
                // spelling of "no profile".
                s.EquipmentProfile ?? "",
                s.Telescope,
                s.PixelScaleArcsec,
                s.RmsRaArcsec,
                s.RmsDecArcsec,
                s.RmsTotalArcsec,
                s.PeakRaArcsec,
                s.PeakDecArcsec,
                s.DropCount,
                s.MaxDropRun,
                s.UnguidedSeconds,
                s.DitherCount,
                s.SettleCount,
                s.SettleFailedCount,
                s.SettleMedianS,
                s.SnrMean,
                s.StarMassMean,
                s.LastCalIssue,
                s.PierSide))
            .AsEnumerable()
            // After the statement, because the resolution is a dictionary lookup EF cannot
            // translate. Phd2SessionSummary.Telescope carries the RESOLVED rig from here on, so
            // the filter below, the rollup's rig rule and the graph's own RigLabel all read one
            // answer and the band never labels a rig from an attribution the user has replaced.
            .Select(row => row with
            {
                Telescope = Phd2Profiles.EffectiveTelescope(row.EquipmentProfile, profiles),
            })
            .ToList();

        if (!string.IsNullOrEmpty(telescope))
        {
            // Spec 7.6 step 1: the rig name is expanded to its canonical form plus every alias
            // before comparing. AliasMap.TelescopeMatchSet is the one implementation of that,
            // shared with the correlation pass, and it folds to canonical inside itself so a
            // second caller cannot reintroduce the raw comparison (phase-review.md F7).
            rows = Phd2Metrics.SelectNightRows(
                rows,
                r => r.Telescope,
                r => r.EquipmentProfile,
                aliases.Current.TelescopeMatchSet(telescope));
        }

        return new Phd2NightGuiding(Phd2Metrics.AggregateNight(rows.Select(ForRollup)), rows);
    }

    /// <summary>Whether the catalogue holds any guide log at all (spec 12.4: the Guiding band is
    /// drawn only on a library that has scanned one). One <c>EXISTS</c> over <c>phd2_logs</c>,
    /// issued once per page open and never per night.</summary>
    /// <remarks>
    /// Every <c>phd2_logs</c> row counts, a failed parse included (spec 5.15): the question is
    /// whether the library has ever seen a guide log, not whether one parsed. A library whose only
    /// log failed to parse is the library that most needs the section to say something, and hiding
    /// the band there would leave it with nowhere to say it.
    /// <para>
    /// This is deliberately not the Statistics empty notice's predicate, which spec 12.5 decides
    /// from <c>GuidingStats</c> alone. Those are session figures and this is a log figure: a
    /// library holding one <c>empty</c> log has a row here and no session, so the band is drawn and
    /// reads "No PHD2 guide logs for this night" while Statistics reads "No PHD2 guide logs
    /// catalogued". Both sentences are true of what their own page counts.
    /// </para>
    /// <para>
    /// Memoised, because the answer is a property of the library rather than of a night and the
    /// page asks it once per night card: on a target with two hundred nights that was two hundred
    /// <c>EXISTS</c> statements where spec 12.4 promises one per page (task3-review.md P2-3). The
    /// memo is dropped by <see cref="InvalidateGuideLogMemo"/> and by nothing else, so a scan that
    /// catalogues a library's first guide log reveals the band on the page rebuild that follows.
    /// </para>
    /// <para>
    /// The lock is not for correctness of the answer, which is idempotent, but because the memo is
    /// read on the UI thread and reset from whatever thread a scan finishes on; a torn read of a
    /// nullable bool is what it prevents. The worst a benign race costs is one extra
    /// <c>EXISTS</c>.
    /// </para>
    /// </remarks>
    public bool AnyGuideLogs()
    {
        lock (_guideLogGate)
        {
            if (_anyGuideLogs is { } memo)
            {
                return memo;
            }
        }

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        var answer = context.Phd2Logs.Any();

        lock (_guideLogGate)
        {
            _anyGuideLogs = answer;
        }

        return answer;
    }

    /// <summary>Drops <see cref="AnyGuideLogs"/>'s memo, so the next call asks the database again.
    /// Called from <c>AppHost.InvalidateDerivedCaches</c>, beside the other derived-data drops, so
    /// one reset serves a scan that catalogued a library's first guide log and a scan that removed
    /// its last one. Safe to call from any thread and safe to call when nothing is memoised.
    /// </summary>
    public void InvalidateGuideLogMemo()
    {
        lock (_guideLogGate)
        {
            _anyGuideLogs = null;
        }
    }

    // The rollup is Phd2Metrics.AggregateNight and takes the ingest side's Phd2SessionMetrics,
    // which is what a stored row is after a round trip (spec 5.16). Every member of that record is
    // init-only with a default, so this projection sets the twelve the rollup reads and leaves the
    // rest at their defaults; the unset members are unread, not forgotten. The twelve are:
    // FrameCount, RmsRaArcsec, RmsDecArcsec, RmsTotalArcsec, DropCount, MaxDropRun,
    // UnguidedSeconds, DitherCount, SettleFailedCount, SettleMedianS, LastCalIssue and
    // EquipmentProfile. The alternative, an AggregateNight overload over a narrow row record, is a
    // Core signature change bought for nothing (questions-a.md Q2).
    private static Phd2SessionMetrics ForRollup(Phd2SessionSummary row) => new()
    {
        FrameCount = row.FrameCount,
        RmsRaArcsec = row.RmsRaArcsec,
        RmsDecArcsec = row.RmsDecArcsec,
        RmsTotalArcsec = row.RmsTotalArcsec,
        DropCount = row.DropCount,
        MaxDropRun = row.MaxDropRun,
        UnguidedSeconds = row.UnguidedSeconds,
        DitherCount = row.DitherCount,
        SettleFailedCount = row.SettleFailedCount,
        SettleMedianS = row.SettleMedianS,
        LastCalIssue = row.LastCalIssue,
        EquipmentProfile = row.EquipmentProfile,
    };
}
