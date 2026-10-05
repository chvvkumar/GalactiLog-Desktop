using System.Text.Json;
using GalactiLog.Core.Phd2;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The ONE reader of the stored <c>phd2_sessions.events</c> document (task2-review.md P3-7).
/// </summary>
/// <remarks>
/// <para>
/// There were two, with different leniency: this one, a strict round trip of the ingest's own
/// write, and a hand-written <c>JsonDocument</c> walk in <c>Phd2Correlation</c> that skipped a
/// malformed entry and kept the rest. Design lesson 1: two readers of one document eventually
/// disagree about what the document says, and the disagreement here would be about which frames an
/// RMS excludes. The strict reader is the survivor because <see cref="Phd2Event"/> is the record
/// the ingest serialised and already carries the three JSON names, and because the web reads the
/// document as a whole and is lenient only per FIELD, which <see cref="JsonSerializer"/> already
/// is: a missing <c>type</c> or <c>detail</c> reads as the empty string
/// (<c>api/phd2.py:152-154</c>).
/// </para>
/// <para>
/// <b>One behaviour changed when the walk went.</b> A document with one structurally malformed
/// entry, an entry whose <c>t</c> is a string say, now yields the EMPTY list to the correlation's
/// window rule where the walk kept the good entries. That document is unreachable except by hand
/// editing the database, since the ingest writes it and the column is required, and the decision
/// is pinned by a case on both paths so it is a decision rather than a drift.
/// </para>
/// </remarks>
internal static class Phd2Events
{
    /// <summary>The stored document in stored order, which is time order. Empty when the document
    /// is not a readable array of entries.</summary>
    public static IReadOnlyList<Phd2Event> Read(string document)
    {
        List<Phd2Event>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<Phd2Event>>(document);
        }
        catch (JsonException)
        {
            return [];
        }

        return parsed is null
            ? []
            : parsed.ConvertAll(e => new Phd2Event(Text(e.Type), e.TimeOffset, Text(e.Detail)));
    }

    private static string Text(string? value) => value ?? "";
}

/// <summary>
/// One guiding session on a night, port of <c>schemas/phd2.py:12-44</c>, filled by
/// <c>api/phd2.py:79-105</c>. Every figure is the stored <c>phd2_sessions</c> row as spec 5.16
/// wrote it, reported whatever the frame count; nothing here is recomputed and nothing is
/// suppressed.
/// </summary>
/// <remarks>
/// <para>
/// This type opens the Guiding band's and the guide graph's read models (spec 12.4), ports of
/// <c>backend/app/schemas/phd2.py</c>. Records only, no behaviour beyond
/// <see cref="Phd2SessionSummary.Gated"/>: every rule that produced a number lives in
/// <see cref="Phd2NightQuery"/> and <see cref="Phd2FramesQuery"/>, or in
/// <see cref="Phd2Metrics"/> where it is shared with the ingest.
/// </para>
/// <para>
/// Three types a reader may expect here are deliberately absent, because each of them already
/// exists and a second copy would be the duplication that design lesson 1 names. The night rollup
/// is <see cref="Phd2NightSummary"/>, which <see cref="Phd2Metrics.AggregateNight"/> produces. The
/// guide-graph event point is <see cref="Phd2Event"/>, the record
/// <c>Phd2Ingest</c> serialised into <c>phd2_sessions.events</c>. The grading baseline is
/// <c>GalactiLog.Core.Metrics.MetricBaseline</c>.
/// </para>
/// <para>
/// <see cref="Phd2FramePoint"/> is named so it cannot be mistaken for either of the two
/// <c>Phd2Frame</c> types this solution already carries: <c>GalactiLog.Core.Phd2.Phd2Frame</c> is
/// the parser's CSV row and <c>GalactiLog.Data.Entities.Phd2Frame</c> is the stored row.
/// </para>
/// </remarks>
/// <param name="Id">The <c>phd2_sessions</c> row's primary key, the argument
/// <see cref="Phd2FramesQuery.Get"/> takes.</param>
/// <param name="StartedAtUtc">Non-nullable although <c>Phd2Session.StartedAtUtc</c> is nullable.
/// Ruling F1 leaves a session with no resolved zone a null <c>started_at_utc</c> and a null
/// <c>session_date</c>, and <see cref="Phd2NightQuery"/> selects on <c>session_date</c>, so an
/// unzoned session is unreachable through that query by construction.</param>
/// <param name="EndedAtUtc">Null for a truncated section.</param>
/// <param name="DurationS">Seconds, as stored and already rounded to three decimals.</param>
/// <param name="FrameCount">Every CSV row of the section, DROP rows included. Load bearing: with
/// <paramref name="PixelScaleArcsec"/> it is what spec 12.4's state table decides on before the
/// band issues a frames query at all.</param>
/// <param name="EquipmentProfile">The header's <c>Equipment Profile</c> line, the empty string
/// when the header named none (<c>api/phd2.py:87</c>). Never null, so the sole-unmapped-profile
/// branch of the rig rule sees one spelling of "no profile".</param>
/// <param name="Telescope">The rig the profile is mapped to. Null means the profile is
/// unmapped.</param>
/// <param name="PixelScaleArcsec">Arcseconds per pixel, per section and never per file. Null on an
/// ASIAIR header, which carries no scale line. Load bearing, see
/// <paramref name="FrameCount"/>.</param>
/// <param name="RmsRaArcsec">Arcseconds, stored and already rounded to six decimals. Null when the
/// section had no pixel scale or no usable frame. <b>Stored whatever the frame count is</b>: spec
/// 5.16's gate is never applied to a stored row, only to a rollup.</param>
/// <param name="RmsDecArcsec">As <paramref name="RmsRaArcsec"/>.</param>
/// <param name="RmsTotalArcsec">The hypotenuse of the two above. Null when either is null.</param>
/// <param name="PeakRaArcsec">Largest absolute RA raw distance among the counted frames,
/// arcseconds. Null on the same terms as the RMS figures.</param>
/// <param name="PeakDecArcsec">As <paramref name="PeakRaArcsec"/>.</param>
/// <param name="DropCount">Rows whose mount field was <c>DROP</c>.</param>
/// <param name="MaxDropRun">Longest consecutive run of those rows.</param>
/// <param name="UnguidedSeconds">Elapsed seconds inside drop runs, stored and already rounded to
/// three decimals.</param>
/// <param name="DitherCount">Dither events in the section.</param>
/// <param name="SettleCount">Settles that completed or failed.</param>
/// <param name="SettleFailedCount">Of those, the ones that failed.</param>
/// <param name="SettleMedianS">Median settle duration, seconds, stored and already rounded to
/// three decimals. Null when no settle both started and finished.</param>
/// <param name="SnrMean">Mean SNR over every row carrying one, stored and already rounded to four
/// decimals. Null when no row carried one.</param>
/// <param name="StarMassMean">Mean star mass in ADU, on the same terms.</param>
/// <param name="LastCalIssue">The header's <c>Last Cal Issue</c> field. Null when the header
/// carried none. The literal <c>None</c> is a value here and is filtered out by the rollup, not by
/// this record.</param>
/// <param name="PierSide">The header's pier side. Null when the header carried none.</param>
public sealed record Phd2SessionSummary(
    Guid Id,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    double DurationS,
    int FrameCount,
    string EquipmentProfile,
    string? Telescope,
    double? PixelScaleArcsec,
    double? RmsRaArcsec,
    double? RmsDecArcsec,
    double? RmsTotalArcsec,
    double? PeakRaArcsec,
    double? PeakDecArcsec,
    int DropCount,
    int MaxDropRun,
    double UnguidedSeconds,
    int DitherCount,
    int SettleCount,
    int SettleFailedCount,
    double? SettleMedianS,
    double? SnrMean,
    double? StarMassMean,
    string? LastCalIssue,
    string? PierSide)
{
    /// <summary>True when the section is too short for its RMS figures to mean anything, which is
    /// <c>api/phd2.py:105</c>'s <c>frame_count &lt; MIN_FRAMES</c>. Derived rather than stored:
    /// spec 5.16 keeps the gate off the stored row, and a rollup that excluded this session's RMS
    /// and a card that printed it as ungated would then be two readings of one rule.</summary>
    public bool Gated => FrameCount < Phd2Metrics.MinFrames;
}

/// <summary>
/// One night's guiding, the whole answer of <see cref="Phd2NightQuery.Get"/>. The web splits this
/// across <c>GET /phd2/sessions</c> and the session-detail night summary
/// (<c>target_detail.py:568-571</c>); the port answers both in one read because one screen shows
/// both.
/// </summary>
/// <param name="Summary"><see cref="Phd2Metrics.AggregateNight"/> over exactly the sessions in
/// <paramref name="Sessions"/> and never over a wider set. An empty night carries the empty rollup,
/// never null.</param>
/// <param name="Sessions">Ordered by <see cref="Phd2SessionSummary.StartedAtUtc"/> ascending, after
/// the rig rule narrowed them. Empty, never null.</param>
public sealed record Phd2NightGuiding(
    Phd2NightSummary Summary,
    IReadOnlyList<Phd2SessionSummary> Sessions);

/// <summary>
/// One guiding CSV row as the guide graph plots it, port of <c>schemas/phd2.py:51-63</c>. The
/// stored row is pixels only; the two arcsecond figures are converted at read time from the
/// owning session's pixel scale, so a corrected scale never rewrites a frame row.
/// </summary>
/// <param name="T">Seconds since the section began.</param>
/// <param name="Ra">RA raw distance in arcseconds, rounded to six decimals by
/// <see cref="Phd2Metrics.ToArcsec"/>. Null when the stored <c>ra_raw</c> is null or the session
/// carries no pixel scale. A null here never means the row is missing.</param>
/// <param name="Dec">As <paramref name="Ra"/>, from <c>dec_raw</c>.</param>
/// <param name="RaPulseMs">The row's RA pulse duration, milliseconds.</param>
/// <param name="RaDir"><c>W</c>, <c>E</c> or the empty string.</param>
/// <param name="DecPulseMs">The row's declination pulse duration, milliseconds.</param>
/// <param name="DecDir"><c>N</c>, <c>S</c> or the empty string.</param>
/// <param name="Snr">Signal to noise as PHD2 wrote it. Null when the row carried none.</param>
/// <param name="Mass">Star mass in ADU. Null when the row carried none.</param>
/// <param name="Dropped">True when the row's mount field was <c>DROP</c>.</param>
public sealed record Phd2FramePoint(
    double T,
    double? Ra,
    double? Dec,
    int RaPulseMs,
    string RaDir,
    int DecPulseMs,
    string DecDir,
    double? Snr,
    double? Mass,
    bool Dropped);

/// <summary>
/// One session's frames and events, the whole answer of <see cref="Phd2FramesQuery.Get"/>. Port of
/// <c>schemas/phd2.py:78-82</c>.
/// </summary>
/// <param name="PixelScaleArcsec">The session's own stored scale, reported and never guessed. Null
/// is neither an error nor a zero: it nulls every <see cref="Phd2FramePoint.Ra"/> and
/// <see cref="Phd2FramePoint.Dec"/> and leaves every other field of every frame populated, which is
/// the pair of facts the graph's no-scale notice fires on.</param>
/// <param name="StartedAtUtc">The clock origin the graph labels its time axis from. <b>Null when
/// the session has no resolved zone</b> (ruling F1), which spec 12.4's range caption, time axis and
/// hover heading degrade to elapsed durations for rather than printing a wrong clock time. Never a
/// substituted local wall clock: <c>Phd2Session.StartedAtLocal</c> and <c>StartedAtUtc</c> both
/// round trip <see cref="DateTimeKind.Unspecified"/>, so no consumer could tell a substitution from
/// the real instant at run time. Reachable although <see cref="Phd2NightQuery"/> cannot return an
/// unzoned session: <c>Phd2Correlation.RederiveSessionTimes</c> nulls <c>started_at_utc</c> on a
/// zone change while the band still holds the id, and this query is keyed on that id.</param>
/// <param name="Frames">Ordered by <see cref="Phd2FramePoint.T"/> ascending. Empty, never
/// null.</param>
/// <param name="Events">The stored <c>phd2_sessions.events</c> document in stored order, which is
/// time order. Empty when the section had none or the document could not be read.</param>
public sealed record Phd2SessionFrames(
    double? PixelScaleArcsec,
    DateTime? StartedAtUtc,
    IReadOnlyList<Phd2FramePoint> Frames,
    IReadOnlyList<Phd2Event> Events);
