using System.Text.RegularExpressions;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>Removes file system paths and URLs from text bound for the telemetry payload. Pure,
/// no I/O.</summary>
public static partial class TelemetryScrubber
{
    public const int MaxLength = 200;

    // One alternation, branches in this order: file URI, scheme URL, UNC path, drive path. The
    // path branches run to the next quote, angle bracket, pipe or line end, so a folder name with a
    // space in it is consumed whole; over-scrubbing the rest of a sentence is the accepted cost.
    [GeneratedRegex(@"file:///?[^""'<>|\r\n]*|(?<url>\b[a-z][a-z0-9+.-]*://[^\s""'<>|]*)|\\\\[^""'<>|\r\n]*|[A-Za-z]:[\\/][^""'<>|\r\n]*")]
    private static partial Regex PathPattern();

    /// <summary>The text with every path replaced by <c>&lt;path&gt;</c> and every other URL by
    /// <c>&lt;url&gt;</c>, truncated to <see cref="MaxLength"/> characters. Null reads as empty.</summary>
    public static string Scrub(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        var scrubbed = PathPattern().Replace(s, m => m.Groups["url"].Success ? "<url>" : "<path>");
        return scrubbed.Length > MaxLength ? scrubbed[..MaxLength] : scrubbed;
    }
}
