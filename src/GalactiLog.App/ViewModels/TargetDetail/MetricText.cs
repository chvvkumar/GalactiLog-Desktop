using System.Globalization;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// The formatting rules the Target detail projections share. Extracted at the second occurrence
/// rather than the sixth: <see cref="SessionCardViewModel"/>, <see cref="TargetTotalsViewModel"/>,
/// <see cref="FilterTableRowViewModel"/> and <see cref="RangeCellViewModel"/> all render the same
/// nullable metrics, and Task 5's frame table renders them again per row.
/// </summary>
internal static class MetricText
{
    /// <summary>
    /// A nullable metric as display text. Null means "no frame carried this metric", never zero
    /// (Task 1 handoff), so an absent metric renders as an empty string and the view hides its
    /// cell rather than showing a misleading 0.
    /// </summary>
    public static string Format(double? value, string format, string suffix = "")
        => value is { } present && double.IsFinite(present)
            ? present.ToString(format, CultureInfo.InvariantCulture) + suffix
            : "";

    /// <summary>A table cell's figure: <see cref="Format"/>, or <see cref="Missing"/> where
    /// <see cref="Format"/> answers empty, so a table never shows a silent blank (spec.md item 6).
    /// A real zero formats as <c>0</c>. <see cref="Format"/> itself keeps its empty string, which
    /// its non-table callers test for.</summary>
    public static string Cell(double? value, string format, string suffix = "")
        => Format(value, format, suffix) is { Length: > 0 } text ? text : Missing;

    /// <inheritdoc cref="Cell(double?, string, string)"/>
    public static string Cell(string? text) => string.IsNullOrEmpty(text) ? Missing : text;

    /// <summary>Hours as a table figure: one decimal, thousands separators, no unit (the heading
    /// carries it).</summary>
    public static string HourFigure(double seconds) => Cell(seconds / 3600d, "N1");

    /// <summary>Hours with one decimal, for example <c>3.2 h</c>. The same rendering
    /// <c>TargetRowViewModel</c> and <c>TargetTotalsViewModel</c> use, so a dashboard row, a
    /// totals row and a session card cannot disagree on the same integration.</summary>
    public static string Hours(double seconds)
        => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600d:0.0} h");

    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The placeholder a figure renders when it has no value: a plain hyphen. The web
    /// renders an em dash there; this repository forbids em dashes outright, and the hyphen
    /// carries the same "nothing to show" meaning.</summary>
    public const string Missing = "-";

    /// <summary>
    /// An integration total as hours, minutes and seconds, ported from
    /// <c>frontend/src/utils/format.ts::formatIntegration</c>: hours and minutes zero-padded to
    /// two digits, the seconds field appended only when the total is not a whole minute.
    /// </summary>
    /// <remarks>
    /// Added by Phase 9 Task 3 for the Statistics page, which renders this figure in eight places.
    /// The collision map's designated-owner table names <see cref="MetricText"/> as the owner of
    /// the web's three format helpers, "extended by 3", so this lives beside <see cref="Hours"/>
    /// rather than in a second formatter under <c>ViewModels/Stats</c>.
    /// <para>
    /// Hours are deliberately not capped at 24 and there is no day field: a library total reads
    /// <c>312h 05m</c>, exactly as the web renders it. The seconds field is conditional because
    /// every figure is formatted from its own rounded-to-the-second value, which is what makes a
    /// breakdown sum exactly to its displayed total (the web source's AUD-027).
    /// </para>
    /// </remarks>
    public static string Integration(double seconds)
    {
        // JavaScript's Math.round is half-up and .NET's default is banker's rounding; they differ
        // only on an exact half second, and AwayFromZero is the matching one for the clamped
        // non-negative total the web computes.
        var total = (long)Math.Max(0d, Math.Round(seconds, MidpointRounding.AwayFromZero));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var remainder = total % 60;
        var head = string.Create(CultureInfo.InvariantCulture, $"{hours:00}h {minutes:00}m");
        return remainder == 0
            ? head
            : head + string.Create(CultureInfo.InvariantCulture, $" {remainder:00}s");
    }

    /// <summary>
    /// A byte count in decimal SI units, ported from
    /// <c>frontend/src/utils/format.ts::formatBytes</c>. 1000-based, not 1024-based: the web
    /// migrated every surface onto this convention (its AUD-025), so a figure on the Statistics
    /// storage card and the same figure on a Settings tab cannot disagree.
    /// </summary>
    public static string Bytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes <= 0)
        {
            return "0 B";
        }

        return bytes switch
        {
            < 1e3 => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes, MidpointRounding.AwayFromZero):0} B"),
            < 1e6 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e3:0} KB"),
            < 1e9 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB"),
            < 1e12 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e12:0.00} TB"),
        };
    }
}

/// <summary>
/// The one rendering of a stored instant as a clock time, from <c>general.timezone</c> and
/// <c>general.use_24h_time</c> (spec 5.8.1). Owned by Phase 6 Task 4 and reused by Task 5's Time
/// column: two renderings of the same instant on the same screen is the failure this exists to
/// prevent (collision-map designated owners).
/// </summary>
/// <remarks>
/// <c>capture_date</c> is stored as ISO-8601 UTC with a <c>Z</c> suffix (spec 7.1.2 rule 4), and
/// <c>SqliteDataReader.GetDateTime</c> hands it back with an unspecified kind, so the instant is
/// pinned to UTC before conversion. <c>general.timezone</c> is display formatting only and never
/// affects <c>session_date</c> (spec 5.8.1).
/// </remarks>
internal static class SessionTimeFormat
{
    /// <summary>
    /// The configured display zone. An empty or unknown id falls back to the machine's local
    /// zone rather than throwing: a settings document carried over from another machine must not
    /// take a page down. <c>FindSystemTimeZoneById</c> accepts both IANA and Windows ids on
    /// .NET 6 and later, so a document written by the web application resolves unchanged.
    /// </summary>
    public static TimeZoneInfo Resolve(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>A stored UTC instant as <c>HH:mm</c> or <c>h:mm tt</c> in the configured zone.
    /// Empty for a frame that carries no capture time.</summary>
    public static string Format(DateTime? utcInstant, TimeZoneInfo zone, bool use24Hour)
    {
        if (utcInstant is not { } instant)
        {
            return "";
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(instant, DateTimeKind.Utc),
            zone);

        return FormatLocal(local, use24Hour);
    }

    /// <summary>The two clock shapes themselves, so a caller that already holds a local wall-clock
    /// time renders it exactly as <see cref="Format"/> renders a converted one.</summary>
    private const string TwentyFourHourClock = "HH:mm";

    private const string TwelveHourClock = "h:mm tt";

    private const string TwentyFourHourClockWithSeconds = "HH:mm:ss";

    private const string TwelveHourClockWithSeconds = "h:mm:ss tt";

    /// <summary>
    /// A local wall-clock time as <c>HH:mm</c> or <c>h:mm tt</c>. The path for anything that is
    /// already local: the night strip's axis marks and its astronomical-night band are computed in
    /// the display zone, and converting them back to UTC so they could go through
    /// <see cref="Format"/> would throw on a local time a daylight-saving jump skipped.
    /// </summary>
    /// <remarks>This exists so the two format strings live in one place rather than being repeated
    /// at the second call site (design-lessons rule 1, coordinator item 1). An axis mark and a
    /// frame table Time cell cannot disagree on shape while both read these.</remarks>
    public static string FormatLocal(DateTime local, bool use24Hour)
        => local.ToString(use24Hour ? TwentyFourHourClock : TwelveHourClock, CultureInfo.InvariantCulture);

    /// <summary>
    /// The same two shapes with seconds, <c>HH:mm:ss</c> or <c>h:mm:ss tt</c>: the guide graph's
    /// axis marks and its hover readout, which annotate a plot whose whole span can be under a
    /// minute and so cannot round a clock time to the minute.
    /// </summary>
    /// <remarks>This lives beside <see cref="FormatLocal"/> for the reason that member exists: the
    /// four clock format strings are declared in one file, so the graph's clock and the frame
    /// table's cannot part (design-lessons rule 1, fixer item 23).</remarks>
    public static string FormatWithSeconds(DateTime local, bool use24Hour)
        => local.ToString(
            use24Hour ? TwentyFourHourClockWithSeconds : TwelveHourClockWithSeconds,
            CultureInfo.InvariantCulture);
}
