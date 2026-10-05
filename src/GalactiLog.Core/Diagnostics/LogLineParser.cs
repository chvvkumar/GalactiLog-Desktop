using System.Globalization;
using System.Text.RegularExpressions;

namespace GalactiLog.Core.Diagnostics;

/// <summary>
/// The one parse of spec 16.1's output template. <c>AppHost</c> passes that template to the
/// Serilog file sink verbatim and this class is written against exactly it:
/// <code>
/// {Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Nothing here throws on content it does not recognize. A log file is an append-only stream a
/// reader can open part-way through a write, so a line the entry-start pattern rejects is a
/// continuation line rather than a parse error, and a leading partial entry is dropped rather than
/// reported.
/// </para>
/// <para>
/// One parser, one definition of "a new entry starts here". A second regex somewhere else would
/// eventually disagree with this one about what a continuation line is (design-lessons rule 1).
/// </para>
/// </remarks>
public static class LogLineParser
{
    // The literal three-letter forms {Level:u3} writes, hardcoded.
    //
    // DO NOT derive this map by truncating the enum name. Verbose truncates to "Ver", not "VRB",
    // and Debug to "Deb", not "DBG": Serilog's u3 specifier abbreviates rather than truncates.
    // That is the single most likely defect in this parser, and
    // LogLineParserTests.TryParseLevel_RejectsATruncatedEnumName pins it.
    private const string VerboseToken = "VRB";
    private const string DebugToken = "DBG";
    private const string InformationToken = "INF";
    private const string WarningToken = "WRN";
    private const string ErrorToken = "ERR";
    private const string FatalToken = "FTL";

    /// <summary>
    /// The template prefix, anchored at the start of the line: the timestamp with its offset, the
    /// bracketed three-letter level, and the single space the template puts before
    /// <c>{SourceContext}</c>. A line this rejects is a continuation of the previous entry.
    /// </summary>
    /// <remarks>
    /// <c>RegexOptions.Compiled | RegexOptions.CultureInvariant</c> with a 250 ms match timeout,
    /// which is <c>NameRuleMatcher</c>'s house rule for every compiled pattern in this assembly.
    /// The pattern has no backtracking of its own, so the timeout is defense in depth rather than
    /// a live concern.
    /// </remarks>
    private static readonly Regex EntryStart = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[(?:VRB|DBG|INF|WRN|ERR|FTL)\] ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>The template's timestamp format. Parsed and formatted with
    /// <see cref="CultureInfo.InvariantCulture"/>, always: Serilog writes with the invariant
    /// culture, so a machine on a non-Gregorian or comma-decimal locale would otherwise fail to
    /// read its own logs.</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    // "2026-09-15" + " " + "12:34:56.789" + " " + "+05:30": fixed width by construction, which is
    // what lets the timestamp be sliced rather than searched for.
    private const int TimestampLength = 30;

    /// <summary>The three-letter forms <c>{Level:u3}</c> writes, mapped to the enum. Ordinal and
    /// case sensitive: Serilog writes the token in upper case and nothing else is a level.
    /// </summary>
    public static bool TryParseLevel(ReadOnlySpan<char> token, out LogLineLevel level)
    {
        switch (token)
        {
            case VerboseToken: level = LogLineLevel.Verbose; return true;
            case DebugToken: level = LogLineLevel.Debug; return true;
            case InformationToken: level = LogLineLevel.Information; return true;
            case WarningToken: level = LogLineLevel.Warning; return true;
            case ErrorToken: level = LogLineLevel.Error; return true;
            case FatalToken: level = LogLineLevel.Fatal; return true;
            default: level = default; return false;
        }
    }

    /// <summary>True when the line begins a new entry, that is, it matches spec 16.1's
    /// template prefix. Anything else is a continuation of the previous entry.</summary>
    public static bool IsEntryStart(string line) => Prefix(line) > 0;

    /// <summary>Parses one entry-start line into its parts. Returns false for a line
    /// <see cref="IsEntryStart"/> rejects.</summary>
    public static bool TryParseHeader(
        string line, out DateTimeOffset timestamp, out LogLineLevel level,
        out string sourceContext, out string message)
    {
        timestamp = default;
        level = default;
        sourceContext = "";
        message = "";

        var prefix = Prefix(line);
        if (prefix == 0)
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                line.AsSpan(0, TimestampLength),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp))
        {
            return false;
        }

        // The regex already restricted the token to the six literals, so this cannot fail; it is
        // called rather than re-derived so the map has exactly one definition.
        if (!TryParseLevel(line.AsSpan(TimestampLength + 2, 3), out level))
        {
            return false;
        }

        // A greedy split at the first space is exact here, and this is why: {SourceContext} is a
        // CLR type name, which never contains a space, and the template puts exactly one space
        // between it and {Message:lj}. An event with no SourceContext renders it as the empty
        // string, which leaves two adjacent spaces, so the split yields an empty token and the
        // message keeps every character after it.
        var rest = line.AsSpan(prefix);
        var space = rest.IndexOf(' ');
        if (space < 0)
        {
            sourceContext = rest.ToString();
            return true;
        }

        sourceContext = rest[..space].ToString();
        message = rest[(space + 1)..].ToString();
        return true;
    }

    // The length of the matched template prefix, or 0 when the line does not start an entry. A
    // catastrophic-backtracking timeout is treated as "not an entry start", the same degradation
    // NameRuleMatcher applies: a reader of an append-only file must never throw on its content.
    private static int Prefix(string line)
    {
        try
        {
            var match = EntryStart.Match(line);
            return match.Success ? match.Length : 0;
        }
        catch (RegexMatchTimeoutException)
        {
            return 0;
        }
    }
}
