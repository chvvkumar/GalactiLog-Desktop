namespace GalactiLog.Core.Diagnostics;

/// <summary>
/// One parsed entry from a Serilog rolling file written with spec 16.1's output template.
/// An entry is one timestamped line plus every continuation line that follows it (an exception
/// and its stack frames), so a stack trace stays with the event that threw.
/// </summary>
/// <param name="Timestamp">The instant the template's <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}</c>
/// carries, offset included.</param>
/// <param name="Level">The template's <c>{Level:u3}</c> token, mapped by
/// <see cref="LogLineParser.TryParseLevel"/>.</param>
/// <param name="SourceContext">The template's <c>{SourceContext}</c>, or the empty string when
/// the event carried none.</param>
/// <param name="Message">The template's <c>{Message:lj}</c>, which is the remainder of the entry's
/// first line.</param>
/// <param name="Exception">The template's <c>{Exception}</c>: every continuation line that
/// followed, joined, or null when the entry was one line.</param>
/// <param name="Ordinal">Position within the file it was read from, oldest = 0, assigned by
/// <see cref="LogReader"/>. It is the tiebreaker half of <see cref="LogCursor"/>, filling the
/// role <c>activity_events.id</c> fills for <c>ActivityCursor</c>: two events written in the
/// same millisecond are ordered by the order they were written, and paging over them is
/// total.</param>
/// <param name="Raw">The entry exactly as the file holds it, header line and continuation lines
/// together. This is what "copy selection" and "copy all" put on the clipboard.</param>
public sealed record LogLine(
    DateTimeOffset Timestamp,
    LogLineLevel Level,
    string SourceContext,
    string Message,
    string? Exception,
    long Ordinal,
    string Raw);

/// <summary>Spec 16.1's six levels, in ascending order so a minimum-level filter is a single
/// comparison. The names match design-spec 5.8.1's general.log_level values and
/// Serilog.Events.LogEventLevel's member names exactly, so AppHost.ParseLevel and this enum
/// cannot drift.</summary>
/// <remarks>
/// GalactiLog.Core has no Serilog reference and must not gain one (ruling Q5), so this is a
/// parallel vocabulary rather than an alias. The equality of the two name sets is pinned by
/// <c>LogViewerViewModelTests.LogLineLevel_NamesMatchSerilogLogEventLevelNames</c>, which lives in
/// GalactiLog.App.Tests because that is the test project which does reference Serilog.
/// </remarks>
public enum LogLineLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}
