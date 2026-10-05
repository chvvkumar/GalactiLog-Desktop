using GalactiLog.Core.Phd2;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// One guiding session's frames and events, converted to arcseconds at read time from that
/// session's own pixel scale (spec 12.4), port of <c>GET /phd2/sessions/{id}/frames</c>
/// (<c>api/phd2.py:110-156</c>).
/// </summary>
/// <remarks>
/// <para>
/// No alias map: nothing here is keyed by equipment. Two statements on a short-lived
/// <see cref="GalactiLogContext"/> opened through
/// <see cref="GalactiLogContextOptions.Create(string, bool)"/> so
/// <c>PragmaConnectionInterceptor</c> stays in the path.
/// </para>
/// <para>
/// <b>The frame read is an index range scan on one key and has to stay one.</b>
/// <c>phd2_frames</c> holds over 1.2 million rows on the user's own library, measured over 60 real
/// guide logs and 937 guiding sections. The statement filters on <c>session_id</c>, which
/// <c>ix_phd2_frames_session_frame</c> and <c>ix_phd2_frames_session_time</c> both lead with, so
/// the database reads one session's range. Materialising first and filtering in memory would
/// return the same rows and would still be correct on a library of that size at a cost no case
/// would show, which is why a source-text case pins the shape as well as the answer.
/// </para>
/// <para>
/// The arcsecond conversion is <see cref="Phd2Metrics.ToArcsec"/> and nothing else. A local
/// multiply and round here, even a one-line one, would make the session RMS and the graph's trace
/// two functions of the same pixels, and the sixth decimal is where they would first disagree.
/// </para>
/// </remarks>
public sealed class Phd2FramesQuery(DatabaseConnectionString connectionString)
{
    /// <summary>Null when no session carries that id. The web raises HTTP 404
    /// (<c>api/phd2.py:124</c>); a null return is the port's house idiom for a missing row, the one
    /// <c>SessionDetailQuery.Get</c> already uses.</summary>
    /// <param name="sessionId">A <see cref="Phd2SessionSummary.Id"/>.</param>
    public Phd2SessionFrames? Get(Guid sessionId)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));

        var parent = context.Phd2Sessions
            .Where(s => s.Id == sessionId)
            .Select(s => new
            {
                s.PixelScaleArcsec,
                s.StartedAtUtc,
                s.Events,
            })
            .FirstOrDefault();
        if (parent is null)
        {
            return null;
        }

        var scale = parent.PixelScaleArcsec;

        var rows = context.Phd2Frames
            .Where(f => f.SessionId == sessionId)
            .OrderBy(f => f.TimeOffset)
            .Select(f => new
            {
                f.TimeOffset,
                f.RaRaw,
                f.DecRaw,
                f.RaDurationMs,
                f.RaDirection,
                f.DecDurationMs,
                f.DecDirection,
                f.Snr,
                f.StarMass,
                f.Dropped,
            })
            .ToList();

        // A null pixel scale nulls Ra and Dec and leaves every other field populated. Never a
        // substituted default, never another session's scale, never an empty frame list and never
        // a throw: the graph's no-scale notice needs the frames present and the scale null
        // together to fire.
        var frames = rows.ConvertAll(f => new Phd2FramePoint(
            f.TimeOffset,
            Phd2Metrics.ToArcsec(f.RaRaw, scale),
            Phd2Metrics.ToArcsec(f.DecRaw, scale),
            f.RaDurationMs,
            f.RaDirection,
            f.DecDurationMs,
            f.DecDirection,
            f.Snr,
            f.StarMass,
            f.Dropped));

        return new Phd2SessionFrames(
            scale,
            // Null when the session has no resolved zone (ruling F1), reported as null and never
            // as the stored local wall clock: spec 12.4 degrades the range caption, the time axis
            // and the hover heading to elapsed durations with no resolvable start rather than
            // printing a wrong clock time, and a substitution here would make that state
            // unrepresentable. Reachable although Phd2NightQuery cannot return such a session:
            // Phd2Correlation.RederiveSessionTimes nulls started_at_utc on a zone change while the
            // band still holds the id, and this query is keyed on the id.
            parent.StartedAtUtc,
            frames,
            // Phd2Events.Read is the one reader of the stored document, shared with the
            // correlation's window rule (task2-review.md P3-7). It used to live here.
            Phd2Events.Read(parent.Events));
    }
}
