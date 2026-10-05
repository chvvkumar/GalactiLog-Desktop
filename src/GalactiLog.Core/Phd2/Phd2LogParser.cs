using System.Globalization;
using System.Text.RegularExpressions;
using GalactiLog.Core.Text;

namespace GalactiLog.Core.Phd2;

// Port of backend/app/services/phd2_parser.py, spec 7.6. Log version 2.5 is the layout every
// pattern here is written against.
//
// Pure text in, records out: no file IO, no database, no settings. The caller reads the whole
// file and hands the text over, which keeps the grammar testable against fixture strings and
// keeps network latency out of the parse. Nothing in this folder opens, creates, writes, moves
// or deletes a file, and FileSafetyTest fails the build if anything here tries.
//
// All timestamps in a guide log are local wall clock with no zone. This file returns them as
// naive values exactly as written, Kind Unspecified; converting to UTC needs the observer
// timezone and belongs to Phd2Metrics, never here (ruling F1).
//
// Three departures from the Python, each deliberate and each named in the Phase 15A report:
// every \d is written [0-9] and every pattern carries RegexOptions.CultureInvariant, so a
// non-ASCII digit is refused at the match rather than captured and then failed by an invariant
// number parser; a value that parses to infinity or NaN is null, matching
// NinaCsvReader.FloatOrNone rather than Python's float(); and the line split covers \r\n, \r
// and \n only rather than everything str.splitlines() treats as a boundary.

/// <summary>The text is a PHD2 guide log, but no run could be read out of it.</summary>
// This exists because "I could not understand this file" and "this file has nothing in it" both
// used to come back as an empty run list, and the caller recorded both as an empty parse. Ten
// real ASIAIR logs sat in that state for a year holding 74 unseen guiding sections, and nothing
// in the product ever said so. It derives from FormatException, the closest analogue of the
// Python's ValueError, so a caller that only cares that the file yielded nothing usable need not
// name this type; deliberately not from InvalidDataException, which lives in the file-handling
// namespace this file is forbidden to name at all.
public sealed class Phd2UnreadableLogException : FormatException
{
    public Phd2UnreadableLogException(string message) : base(message) { }
}

public static class Phd2LogParser
{
    // Byte-stable across all 800 guiding sections in the reference corpus, and used as the
    // state-machine trigger rather than a fuzzy prefix match.
    public const string GuideCsvHeader =
        "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,"
        + "DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,"
        + "XStep,YStep,StarMass,SNR,ErrorCode";

    public const string CalCsvHeader = "Direction,Step,dx,dy,x,y,Dist";

    private const string LogTimeFormat = "yyyy-MM-dd HH:mm:ss";

    // The "Last Cal Issue" line carries a United States twelve-hour timestamp, a different
    // format from every other timestamp in the file. The single-letter forms accept both the
    // corpus's unpadded 7/14/2026 9:42:27 PM and a zero-padded variant; MM/dd/yyyy hh would
    // reject the first. Only the invariant culture parses AM and PM whatever the host locale is.
    private const string CalTimeFormat = "M/d/yyyy h:mm:ss tt";

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private const string Ts = @"([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2})";

    // Line prefixes that only ever appear in a PHD2 guide log, tested on the trimmed line. Used
    // solely to tell a file the parser could not understand apart from a file with nothing in
    // it: a file carrying any of these is a guide log whatever else is wrong with it.
    private static readonly string[] GuideLogMarkers =
    {
        "PHD2 version", "Guiding Begins at", "Guiding Ends at", "Calibration Begins at",
        "Calibration complete,", "Log Summary:", "Log closed at", "Frame,Time", "Direction,Step",
    };

    // The version and platform tag are one optional unit. Desktop builds write
    // "PHD2 version 2.6.14 [Windows], ..."; the PHD2 embedded in an ASIAIR writes
    // "PHD2 version, ..." with both left out. Ten corpus files use the latter and matched
    // nothing at all until the group was made optional.
    private static readonly Regex BannerRe = new(
        @"^PHD2 version(?: (\S+) \[([^\]]*)\])?, Log version ([0-9.]+)\. Log enabled at " + Ts, Opts);

    private static readonly Regex LogClosedRe = new(@"^Log closed at " + Ts, Opts);

    private static readonly Regex LogSummaryRe = new(
        @"^Log Summary: calcnt:([0-9]+) gcnt:([0-9]+) gdur:([0-9]+) gacnt:([0-9]+)", Opts);

    private static readonly Regex GuidingBeginsRe = new(@"^Guiding Begins at " + Ts, Opts);
    private static readonly Regex GuidingEndsRe = new(@"^Guiding Ends at " + Ts, Opts);
    private static readonly Regex CalBeginsRe = new(@"^Calibration Begins at " + Ts, Opts);
    private static readonly Regex CalCompleteRe = new(@"^Calibration complete, mount = (.+?)\.?$", Opts);

    // Only the West and North legs measure a rate and an angle; East and South re-centre the
    // star and never emit a completion line.
    private static readonly Regex AxisCompleteRe = new(
        @"^(West|North) calibration complete\. Angle = ([-0-9.]+) deg, "
        + @"Rate = ([-0-9.]+) px/sec, Parity = (\S+)", Opts);

    private static readonly Regex EvSettleRe = new(@"^INFO: SETTLING STATE CHANGE, (.+)$", Opts);
    private static readonly Regex EvDitherRe = new(@"^INFO: DITHER by (.+)$", Opts);
    private static readonly Regex EvLockRe = new(@"^INFO: SET LOCK POSITION, (.+)$", Opts);
    private static readonly Regex EvStarLostRe = new(@"^INFO: STAR LOST during calibration, (.+)$", Opts);
    private static readonly Regex EvParamRe = new(@"^INFO: Guiding parameter change, (.+)$", Opts);

    private static readonly Regex HProfileRe = new(@"^Equipment Profile = (.+)$", Opts);

    private static readonly Regex HPixScaleRe = new(
        @"^Pixel scale = ([0-9.]+) arc-sec/px"
        + @"(?:, Binning = ([0-9]+))?(?:, Focal length = ([0-9.]+) mm)?", Opts);

    private static readonly Regex HCameraRe = new(@"^Camera = ([^,]+)", Opts);
    private static readonly Regex HExposureRe = new(@"^Exposure = ([0-9.]+) ms", Opts);
    private static readonly Regex HMountRe = new(@"^Mount = ([^,]+)", Opts);

    // The eleven sub-search patterns below carry no ^ and must stay unanchored: the Python
    // searches the whole line for them rather than matching from its start.
    private static readonly Regex HXAngleRe = new(@"xAngle = ([-0-9.]+)", Opts);
    private static readonly Regex HXRateRe = new(@"xRate = ([-0-9.]+)", Opts);
    private static readonly Regex HYAngleRe = new(@"yAngle = ([-0-9.]+)", Opts);
    private static readonly Regex HYRateRe = new(@"yRate = ([-0-9.]+)", Opts);
    private static readonly Regex HParityRe = new(@"parity = (\S+)", Opts);

    private static readonly Regex HNormRatesRe = new(
        @"^Norm rates RA = ([0-9.]+)""/s.*?Dec = ([0-9.]+)""/s"
        + @"(?:; ortho\.err\. = ([-0-9.]+) deg)?", Opts);

    private static readonly Regex HXAlgoRe = new(@"^X guide algorithm = ([^,]+)", Opts);
    private static readonly Regex HYAlgoRe = new(@"^Y guide algorithm = ([^,]+)", Opts);
    private static readonly Regex HHystRe = new(@"Hysteresis = ([0-9.]+)", Opts);
    private static readonly Regex HMinMoveRe = new(@"Minimum move = ([0-9.]+)", Opts);
    private static readonly Regex HAggrRe = new(@"Aggression = ([0-9.]+)", Opts);
    private static readonly Regex HBacklashRe = new(@"^Backlash comp = (\w+)(?:, pulse = ([0-9.]+) ms)?", Opts);

    // The ASIAIR build prefixes this line with a placeholder calibration step; desktop builds
    // start it at "Max RA duration". Without the optional prefix the whole line is unrecognised
    // and all three fields are dropped, which cost all 74 ASIAIR sections in the corpus.
    private static readonly Regex HMaxDurRe = new(
        @"^(?:Calibration step = [^,]+, )?"
        + @"Max RA duration = ([0-9.]+), Max DEC duration = ([0-9.]+)"
        + @"(?:, DEC guide mode = (\w+))?", Opts);

    private static readonly Regex HSpeedsRe = new(
        @"^RA Guide Speed = ([0-9.]+) a-s/s, Dec Guide Speed = ([0-9.]+) a-s/s", Opts);

    private static readonly Regex HCalDecRe = new(@"Cal Dec = ([-0-9.]+)", Opts);
    private static readonly Regex HLastCalRe = new(@"Last Cal Issue = ([^,]+)", Opts);

    // The ASIAIR writes "Timestamp = Unknown", which this does not match at all, so the field
    // stays null and nothing throws.
    private static readonly Regex HCalTsRe = new(
        @"Timestamp = ([0-9]{1,2}/[0-9]{1,2}/[0-9]{4} [0-9]{1,2}:[0-9]{2}:[0-9]{2} [AP]M)", Opts);

    // Two shapes in the corpus: the desktop seven-field line, and the ASIAIR line that omits RA,
    // Alt and Az entirely. Both optional groups are greedy, so a desktop line still fills all
    // seven rather than stopping at Rotator pos. Anchoring on "Dec = " is safe: it is the only
    // line in any corpus file that starts that way, and "Dec Guide Speed" only appears mid-line.
    private static readonly Regex HPointingRe = new(
        @"^(?:RA = ([-0-9.]+) hr, )?"
        + @"Dec = ([-0-9.]+) deg, Hour angle = ([-0-9.]+) hr, "
        + @"Pier side = (\w+), Rotator pos = ([^,]+)"
        + @"(?:, Alt = ([-0-9.]+) deg, Az = ([-0-9.]+) deg)?", Opts);

    private static readonly Regex HLockRe = new(
        @"^Lock position = ([-0-9.]+), ([-0-9.]+), "
        + @"Star position = ([-0-9.]+), ([-0-9.]+), HFD = ([-0-9.]+) px", Opts);

    private enum Mode { None, GuideHeader, GuideRows, GuideDiscard, CalHeader, CalRows }

    // Splits on the same three separators String.Split used (\r\n checked before a lone \r or
    // \n, exactly as a multi-entry Split prefers the earlier array entry at a position where
    // more than one would match), one line at a time rather than as a whole-file array. Python's
    // str.splitlines() also splits on vertical tab, form feed, the file, group and record
    // separators, U+0085, U+2028 and U+2029. A guide log is ASCII written a line at a time by a
    // C++ ofstream and none of those appears in the corpus; if one ever did inside a CSV row,
    // splitting on it would cut the row into two unparsable halves and trigger a discard
    // cascade, while carrying it whole costs at most one row.
    //
    // Only the current line is ever alive, so a multi-megabyte guide log does not pay for a
    // second full-size copy of itself in a string[] the way the whole-file Split it replaces did
    // (phase review P2-1, Task 2 review P3-3). The public Parse(string) signature is unchanged.
    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                yield return text[start..i];
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            else if (c == '\n')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>Parse a whole guide-log file into its stacked runs.</summary>
    // Returns an empty list for text carrying no guide-log content at all, which callers treat
    // as a data-free log rather than an error. Text that clearly is a guide log but yields no
    // run throws instead, so an unreadable file can never again be filed alongside the genuinely
    // empty ones.
    public static IReadOnlyList<Phd2Run> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var runs = new List<Phd2Run>();
        Phd2Run? run = null;
        Phd2Section? section = null;
        Phd2Calibration? calibration = null;
        var mode = Mode.None;
        var lastT = 0.0;

        // A section still open when the file, the run or the next block starts was cut short:
        // PHD2 was killed, or the run boundary swallowed its end line. A section that already
        // has its end line is not truncated, whatever follows it.
        void AbandonSection()
        {
            if (section is not null && section.EndedAtLocal is null)
            {
                section.Truncated = true;
            }
            section = null;
        }

        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var m = BannerRe.Match(line);
            if (m.Success)
            {
                AbandonSection();
                calibration = null;
                mode = Mode.None;
                run = new Phd2Run
                {
                    Phd2Version = GroupOrNull(m, 1),
                    Platform = GroupOrNull(m, 2),
                    LogVersion = m.Groups[3].Value,
                    LogEnabledAt = ParseTimestamp(m.Groups[4].Value),
                };
                runs.Add(run);
                continue;
            }

            // Everything before the first banner is discarded.
            if (run is null) continue;

            m = LogClosedRe.Match(line);
            if (m.Success)
            {
                AbandonSection();
                calibration = null;
                mode = Mode.None;
                run.LogClosedAt = ParseTimestamp(m.Groups[1].Value);
                continue;
            }

            m = LogSummaryRe.Match(line);
            if (m.Success)
            {
                // Closes nothing and changes no mode: PHD2 writes it beside the close line.
                run.Summary = new Phd2LogSummary(
                    ParseInt(m.Groups[1].Value) ?? 0,
                    ParseInt(m.Groups[2].Value) ?? 0,
                    ParseInt(m.Groups[3].Value) ?? 0,
                    ParseInt(m.Groups[4].Value) ?? 0);
                continue;
            }

            m = GuidingBeginsRe.Match(line);
            if (m.Success)
            {
                AbandonSection();
                calibration = null;
                section = new Phd2Section { StartedAtLocal = ParseTimestamp(m.Groups[1].Value) };
                run.Sections.Add(section);
                mode = Mode.GuideHeader;
                lastT = 0.0;
                continue;
            }

            m = GuidingEndsRe.Match(line);
            if (m.Success)
            {
                if (section is null)
                {
                    run.OrphanGuidingEnds++;
                }
                else
                {
                    section.EndedAtLocal = ParseTimestamp(m.Groups[1].Value);
                    section = null;
                }
                mode = Mode.None;
                continue;
            }

            m = CalBeginsRe.Match(line);
            if (m.Success)
            {
                AbandonSection();
                calibration = new Phd2Calibration { StartedAtLocal = ParseTimestamp(m.Groups[1].Value) };
                run.Calibrations.Add(calibration);
                mode = Mode.CalHeader;
                continue;
            }

            m = CalCompleteRe.Match(line);
            if (m.Success)
            {
                if (calibration is not null)
                {
                    calibration.Completed = true;
                    calibration.MountName = m.Groups[1].Value.Trim();
                    calibration = null;
                }
                mode = Mode.None;
                continue;
            }

            m = AxisCompleteRe.Match(line);
            if (m.Success)
            {
                // Deliberately no mode change: more step rows follow the completion line.
                if (calibration is not null)
                {
                    if (m.Groups[1].Value == "West")
                    {
                        calibration.WestAngleDeg = ParseDouble(m.Groups[2].Value);
                        calibration.WestRatePxS = ParseDouble(m.Groups[3].Value);
                        calibration.WestParity = m.Groups[4].Value;
                    }
                    else
                    {
                        calibration.NorthAngleDeg = ParseDouble(m.Groups[2].Value);
                        calibration.NorthRatePxS = ParseDouble(m.Groups[3].Value);
                        calibration.NorthParity = m.Groups[4].Value;
                    }
                }
                continue;
            }

            if (string.Equals(line, GuideCsvHeader, StringComparison.Ordinal))
            {
                mode = Mode.GuideRows;
                continue;
            }

            if (string.Equals(line, CalCsvHeader, StringComparison.Ordinal))
            {
                mode = Mode.CalRows;
                continue;
            }

            if (line.StartsWith("Frame,Time", StringComparison.Ordinal)
                || line.StartsWith("Direction,Step", StringComparison.Ordinal))
            {
                // The two matches above are byte comparisons against the Log version 2.5 layout.
                // A PHD2 release that adds or renames one column would otherwise never enter row
                // mode: every frame silently dropped, a frame count of zero, no error, no
                // warning, and nothing in the file to point at. Say so instead.
                run.Warnings.Add(
                    "CSV header line does not match the known PHD2 2.5 layout, so its rows were "
                    + "not parsed: " + Truncate(line));
                continue;
            }

            if (line.StartsWith("INFO:", StringComparison.Ordinal))
            {
                var ev = ParseEvent(line, lastT);
                if (ev is not null && section is not null)
                {
                    section.Events.Add(ev);
                }
                continue;
            }

            if (mode == Mode.GuideRows && section is not null)
            {
                var frame = ParseFrameRow(line);
                if (frame is null)
                {
                    section.Truncated = true;
                    section.DiscardedRows++;
                    // Keep counting rather than dropping straight to None: the rows after a
                    // corrupt one are still CSV rows, and the count of them is what tells a user
                    // whether they lost a truncated tail or the body of a session. Every
                    // structural line is matched earlier in this loop, so discard mode cannot
                    // swallow one.
                    mode = Mode.GuideDiscard;
                    continue;
                }
                lastT = frame.TimeOffset;
                section.Frames.Add(frame);
                continue;
            }

            if (mode == Mode.GuideDiscard && section is not null)
            {
                section.DiscardedRows++;
                continue;
            }

            if (mode == Mode.CalRows && calibration is not null)
            {
                var step = ParseCalStep(line);
                if (step is not null)
                {
                    calibration.Steps.Add(step);
                }
                continue;
            }

            if (mode == Mode.GuideHeader && section is not null)
            {
                ApplyHeaderLine(section.Header, rawLine);
                continue;
            }

            if (mode == Mode.CalHeader && calibration is not null)
            {
                ApplyHeaderLine(calibration.Header, rawLine);
            }
        }

        AbandonSection();

        if (runs.Count == 0)
        {
            var marker = FirstMarkerLine(text);
            if (marker is not null)
            {
                throw new Phd2UnreadableLogException(
                    "no PHD2 run could be read from this guide log, yet it carries guide-log "
                    + "content. The first line the parser could not place was: " + Truncate(marker));
            }
        }

        WarnDiscardedRows(runs);
        CrossCheckSummaries(runs);
        return runs;
    }

    /// <summary>Fold one header line into <paramref name="header"/>; true when recognised.</summary>
    // The line is trimmed first because some PHD2 builds emit the Camera line with a leading
    // space and the ASIAIR emits Equipment Profile and Mount lines with a trailing one. The
    // patterns are tried in this order and the first match wins, which is what keeps
    // "RA Guide Speed = " and "RA = " from being confused for one another. An unrecognised line
    // returns false and is discarded, which is normal: "Dither = both axes, ..." and
    // "Search region = 15 px, ..." are header lines the parser deliberately ignores.
    public static bool ApplyHeaderLine(Phd2Header header, string rawLine)
    {
        ArgumentNullException.ThrowIfNull(rawLine);

        var line = rawLine.Trim();

        var m = HProfileRe.Match(line);
        if (m.Success)
        {
            header.EquipmentProfile = m.Groups[1].Value.Trim();
            return true;
        }

        m = HPixScaleRe.Match(line);
        if (m.Success)
        {
            header.PixelScaleArcsec = ParseDouble(m.Groups[1].Value);
            header.Binning = ParseInt(GroupOrNull(m, 2));
            header.FocalLengthMm = ParseDouble(GroupOrNull(m, 3));
            return true;
        }

        m = HCameraRe.Match(line);
        if (m.Success)
        {
            header.GuideCamera = m.Groups[1].Value.Trim();
            return true;
        }

        m = HExposureRe.Match(line);
        if (m.Success)
        {
            header.ExposureMs = ParseDouble(m.Groups[1].Value);
            return true;
        }

        m = HMountRe.Match(line);
        if (m.Success)
        {
            header.MountName = m.Groups[1].Value.Trim();
            // Five sub-searches over the same line. The calibration variant of the line is
            // shorter and they simply find nothing.
            if (HXAngleRe.Match(line) is { Success: true } x) header.XAngleDeg = ParseDouble(x.Groups[1].Value);
            if (HXRateRe.Match(line) is { Success: true } xr) header.XRatePxS = ParseDouble(xr.Groups[1].Value);
            if (HYAngleRe.Match(line) is { Success: true } y) header.YAngleDeg = ParseDouble(y.Groups[1].Value);
            if (HYRateRe.Match(line) is { Success: true } yr) header.YRatePxS = ParseDouble(yr.Groups[1].Value);
            // Parity is a string and never goes through the number parser.
            if (HParityRe.Match(line) is { Success: true } p) header.Parity = p.Groups[1].Value;
            return true;
        }

        m = HNormRatesRe.Match(line);
        if (m.Success)
        {
            header.NormRateRa = ParseDouble(m.Groups[1].Value);
            header.NormRateDec = ParseDouble(m.Groups[2].Value);
            header.OrthoErrorDeg = ParseDouble(GroupOrNull(m, 3));
            return true;
        }

        m = HXAlgoRe.Match(line);
        if (m.Success)
        {
            header.AlgoRa = m.Groups[1].Value.Trim();
            if (HHystRe.Match(line) is { Success: true } h) header.HysteresisRa = ParseDouble(h.Groups[1].Value);
            if (HMinMoveRe.Match(line) is { Success: true } mm) header.MinMoveRa = ParseDouble(mm.Groups[1].Value);
            if (HAggrRe.Match(line) is { Success: true } a) header.AggressionRa = ParseDouble(a.Groups[1].Value);
            return true;
        }

        m = HYAlgoRe.Match(line);
        if (m.Success)
        {
            // The Y line carries no Hysteresis and no commas between its fields.
            header.AlgoDec = m.Groups[1].Value.Trim();
            if (HMinMoveRe.Match(line) is { Success: true } mm) header.MinMoveDec = ParseDouble(mm.Groups[1].Value);
            if (HAggrRe.Match(line) is { Success: true } a) header.AggressionDec = ParseDouble(a.Groups[1].Value);
            return true;
        }

        m = HBacklashRe.Match(line);
        if (m.Success)
        {
            header.BacklashCompEnabled =
                string.Equals(m.Groups[1].Value, "enabled", StringComparison.OrdinalIgnoreCase);
            header.BacklashPulseMs = ParseDouble(GroupOrNull(m, 2));
            return true;
        }

        m = HMaxDurRe.Match(line);
        if (m.Success)
        {
            header.MaxRaDurationMs = ParseDouble(m.Groups[1].Value);
            header.MaxDecDurationMs = ParseDouble(m.Groups[2].Value);
            header.DecGuideMode = GroupOrNull(m, 3);
            return true;
        }

        m = HSpeedsRe.Match(line);
        if (m.Success)
        {
            header.RaGuideSpeed = ParseDouble(m.Groups[1].Value);
            header.DecGuideSpeed = ParseDouble(m.Groups[2].Value);
            if (HCalDecRe.Match(line) is { Success: true } c) header.CalDecDeg = ParseDouble(c.Groups[1].Value);
            if (HLastCalRe.Match(line) is { Success: true } l) header.LastCalIssue = l.Groups[1].Value.Trim();
            if (HCalTsRe.Match(line) is { Success: true } t)
            {
                header.CalTimestamp = DateTime.TryParseExact(
                    t.Groups[1].Value, CalTimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null;
            }
            return true;
        }

        m = HPointingRe.Match(line);
        if (m.Success)
        {
            header.RaHr = ParseDouble(GroupOrNull(m, 1));
            header.DecDeg = ParseDouble(m.Groups[2].Value);
            header.HourAngleHr = ParseDouble(m.Groups[3].Value);
            header.PierSide = m.Groups[4].Value;
            header.RotatorPos = m.Groups[5].Value.Trim();
            header.AltDeg = ParseDouble(GroupOrNull(m, 6));
            header.AzDeg = ParseDouble(GroupOrNull(m, 7));
            return true;
        }

        m = HLockRe.Match(line);
        if (m.Success)
        {
            header.LockX = ParseDouble(m.Groups[1].Value);
            header.LockY = ParseDouble(m.Groups[2].Value);
            header.StarX = ParseDouble(m.Groups[3].Value);
            header.StarY = ParseDouble(m.Groups[4].Value);
            header.HfdPx = ParseDouble(m.Groups[5].Value);
            return true;
        }

        return false;
    }

    // Classify an INFO line. An unknown INFO line returns null and is dropped, which is correct:
    // PHD2 writes many. A settling detail that is none of the three known prefixes is dropped
    // too, rather than becoming a settle event that closes a window which never closed.
    private static Phd2Event? ParseEvent(string line, double timeOffset)
    {
        var m = EvSettleRe.Match(line);
        if (m.Success)
        {
            var detail = m.Groups[1].Value.Trim();
            if (detail.StartsWith("settling started", StringComparison.OrdinalIgnoreCase))
                return new Phd2Event(Phd2EventTypes.SettleStart, timeOffset, detail);
            if (detail.StartsWith("settling complete", StringComparison.OrdinalIgnoreCase))
                return new Phd2Event(Phd2EventTypes.SettleDone, timeOffset, detail);
            if (detail.StartsWith("settling failed", StringComparison.OrdinalIgnoreCase))
                return new Phd2Event(Phd2EventTypes.SettleFailed, timeOffset, detail);
            return null;
        }

        m = EvDitherRe.Match(line);
        if (m.Success) return new Phd2Event(Phd2EventTypes.Dither, timeOffset, m.Groups[1].Value.Trim());

        m = EvLockRe.Match(line);
        if (m.Success) return new Phd2Event(Phd2EventTypes.LockShift, timeOffset, m.Groups[1].Value.Trim());

        m = EvStarLostRe.Match(line);
        if (m.Success) return new Phd2Event(Phd2EventTypes.StarLost, timeOffset, m.Groups[1].Value.Trim());

        m = EvParamRe.Match(line);
        if (m.Success) return new Phd2Event(Phd2EventTypes.ParamChange, timeOffset, m.Groups[1].Value.Trim());

        return null;
    }

    // One guiding CSV row, or null when the row is unusable. A row shorter than 18 fields is a
    // file truncated mid-write. The split honours quoting rather than splitting on commas: a
    // DROP row carries a 19th quoted field holding the star-loss reason.
    private static Phd2Frame? ParseFrameRow(string line)
    {
        var cells = CsvLine.Split(line);
        if (cells.Count < 18) return null;
        if (ParseInt(cells[0]) is not { } frameIndex) return null;
        if (ParseDouble(cells[1]) is not { } timeOffset) return null;

        return new Phd2Frame(
            FrameIndex: frameIndex,
            TimeOffset: timeOffset,
            Dropped: string.Equals(cells[2].Trim().Trim('"'), "DROP", StringComparison.OrdinalIgnoreCase),
            Dx: ParseDouble(cells[3]),
            Dy: ParseDouble(cells[4]),
            RaRaw: ParseDouble(cells[5]),
            DecRaw: ParseDouble(cells[6]),
            RaGuide: ParseDouble(cells[7]),
            DecGuide: ParseDouble(cells[8]),
            // Not nullable: an absent, blank or unparseable duration is zero, as is a literal 0.0.
            RaDurationMs: ParseInt(cells[9]) ?? 0,
            RaDirection: cells[10].Trim(),
            DecDurationMs: ParseInt(cells[11]) ?? 0,
            DecDirection: cells[12].Trim(),
            // Indices 13 and 14, XStep and YStep, are read past and stored nowhere.
            StarMass: ParseDouble(cells[15]),
            Snr: ParseDouble(cells[16]),
            ErrorCode: ParseInt(cells[17]),
            DropReason: cells.Count > 18 ? cells[18].Trim() : "");
    }

    // Exactly seven comma-separated fields, not six and not eight. All six numeric fields must
    // parse or the row is dropped, which is why they are not nullable on the record. A dropped
    // step row is silent: it is not counted and it raises no warning.
    private static Phd2CalibrationStep? ParseCalStep(string line)
    {
        var cells = line.Split(',');
        if (cells.Length != 7) return null;
        if (ParseInt(cells[1]) is not { } step) return null;
        if (ParseDouble(cells[2]) is not { } dx) return null;
        if (ParseDouble(cells[3]) is not { } dy) return null;
        if (ParseDouble(cells[4]) is not { } x) return null;
        if (ParseDouble(cells[5]) is not { } y) return null;
        if (ParseDouble(cells[6]) is not { } dist) return null;
        return new Phd2CalibrationStep(cells[0].Trim(), step, dx, dy, x, y, dist);
    }

    // The one number rule for this file: fifteen header patterns do not each carry their own.
    // Empty cells and the literal N/A both mean absent. Departure from the Python, shared with
    // NinaCsvReader.FloatOrNone: a value that parses to infinity or NaN is null, because 1e400
    // parses successfully and is not NaN, and a NaN pixel value would propagate through a mean
    // and a standard deviation and turn a whole session's RMS into NaN with nothing to point at.
    private static double? ParseDouble(string? value)
    {
        if (value is null) return null;
        var text = value.Trim();
        if (text.Length == 0 || string.Equals(text, "N/A", StringComparison.OrdinalIgnoreCase)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return null;
        return double.IsFinite(f) ? f : null;
    }

    // Range-checked before the cast, exactly as NinaCsvReader.IntOrNone is: an unchecked (int)
    // of an out-of-range double yields int.MinValue on x64 rather than failing. Going through
    // ParseDouble is why ErrorCode reads 7 from both "7" and "7.0".
    private static int? ParseInt(string? value)
        => ParseDouble(value) is { } f && f >= int.MinValue && f <= int.MaxValue ? (int)f : null;

    // Naive local wall clock, Kind Unspecified, exactly as written. A failure is null.
    private static DateTime? ParseTimestamp(string value)
        => DateTime.TryParseExact(
            value.Trim(), LogTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    // A group that did not participate has Success false and an empty Value, which is a
    // different thing from a group that captured an empty string.
    private static string? GroupOrNull(Match m, int index)
        => m.Groups[index].Success ? m.Groups[index].Value : null;

    private static string Truncate(string line) => line.Length > 200 ? line[..200] : line;

    private static string? FirstMarkerLine(string text)
    {
        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.Trim();
            foreach (var marker in GuideLogMarkers)
            {
                if (line.StartsWith(marker, StringComparison.Ordinal)) return line;
            }
        }
        return null;
    }

    // The parser cannot recover a section once a row fails: the file may be truncated mid-write,
    // and guessing at partial rows would invent data. What it can do is say how much was lost,
    // so a reader can tell a clipped tail apart from a section that was effectively discarded.
    private static void WarnDiscardedRows(List<Phd2Run> runs)
    {
        foreach (var run in runs)
        {
            for (var index = 0; index < run.Sections.Count; index++)
            {
                var section = run.Sections[index];
                if (section.DiscardedRows == 0) continue;
                var beginning = section.StartedAtLocal is { } started
                    ? started.ToString(LogTimeFormat, CultureInfo.InvariantCulture)
                    : "an unknown time";
                run.Warnings.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"guiding section {index + 1} beginning {beginning}: {section.DiscardedRows} CSV row(s) discarded after an unparsable row"));
            }
        }
    }

    // A free self-test: PHD2 writes how many calibrations and guiding sections it believes it
    // emitted. A mismatch is a warning and never an error, because a run killed before its
    // summary is written is normal.
    private static void CrossCheckSummaries(List<Phd2Run> runs)
    {
        foreach (var run in runs)
        {
            if (run.Summary is not { } summary) continue;
            if (run.Sections.Count != summary.GuidingCount)
            {
                run.Warnings.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Log Summary gcnt:{summary.GuidingCount} but parsed {run.Sections.Count} guiding sections"));
            }
            if (run.Calibrations.Count != summary.CalibrationCount)
            {
                run.Warnings.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Log Summary calcnt:{summary.CalibrationCount} but parsed {run.Calibrations.Count} calibration blocks"));
            }
        }
    }
}
