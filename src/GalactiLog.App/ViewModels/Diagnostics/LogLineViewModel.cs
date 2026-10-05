using System.Globalization;
using GalactiLog.Core.Diagnostics;

namespace GalactiLog.App.ViewModels.Diagnostics;

/// <summary>
/// One row of spec 12.8's log viewer: a parsed entry, preformatted for display.
/// </summary>
/// <remarks>
/// <para>
/// No brush property. The level's colour comes from XAML styles selected by the
/// <see cref="IsWarning"/> and <see cref="IsError"/> classes, so the colours are theme tokens
/// resolved on the UI thread (TRACKING section 6 item 24). If a brush ever becomes unavoidable
/// here it is an <c>ImmutableSolidColorBrush</c> built on the constructing thread, and the
/// constructing thread is the UI thread because the publish is posted.
/// </para>
/// <para>
/// A record, so a follow-tail reload that reads the same entries produces equal rows rather than
/// a list that differs only by reference.
/// </para>
/// </remarks>
/// <param name="Timestamp">Preformatted with the invariant culture, in the same shape the log file
/// itself carries, so a row on screen and a line in the file are comparable character for
/// character.</param>
/// <param name="Level">The enum member name, which is also spec 5.8.1's <c>general.log_level</c>
/// vocabulary and the key the view's level style selects on.</param>
/// <param name="Raw">The entry exactly as the file holds it, continuation lines included. This is
/// what "copy selection" puts on the clipboard.</param>
public sealed record LogLineViewModel(
    string Timestamp,
    string Level,
    string SourceContext,
    string Message,
    string? Exception,
    string Raw)
{
    /// <summary>Builds a row from a parsed entry. Formatting is
    /// <see cref="CultureInfo.InvariantCulture"/> throughout, matching the parse.</summary>
    public static LogLineViewModel From(LogLine line) => new(
        line.Timestamp.ToString(LogLineParser.TimestampFormat, CultureInfo.InvariantCulture),
        line.Level.ToString(),
        line.SourceContext,
        line.Message,
        line.Exception,
        line.Raw);

    /// <summary>Whether the entry carried continuation lines, which is what the view binds the
    /// exception block's visibility to.</summary>
    public bool HasException => Exception is not null;

    /// <summary>The level is <c>Warning</c>. Bound to the row's <c>warn</c> style class, which is
    /// how the level token colour is chosen without a brush on this record (review finding M2).
    /// </summary>
    public bool IsWarning => Level == nameof(LogLineLevel.Warning);

    /// <summary>The level is <c>Error</c> or <c>Fatal</c>. One class for both, because a reader
    /// scanning a log wants the same visual answer to "did something break" from either.</summary>
    public bool IsError =>
        Level == nameof(LogLineLevel.Error) || Level == nameof(LogLineLevel.Fatal);
}
