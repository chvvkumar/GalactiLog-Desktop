using System.Text.RegularExpressions;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>Removes file system paths from text bound for the telemetry payload. Pure, no I/O.
/// </summary>
public static partial class TelemetryScrubber
{
    public const int MaxLength = 200;

    private const string Replacement = "<path>";

    // Drive paths, UNC paths and file URIs, in that order.
    [GeneratedRegex(@"[A-Za-z]:[\\/][^\s""'<>|]*|\\\\[^\s""'<>|]+|file:///?[^\s""'<>|]*")]
    private static partial Regex PathPattern();

    /// <summary>The text with every path replaced by <c>&lt;path&gt;</c>, truncated to
    /// <see cref="MaxLength"/> characters. Null reads as empty.</summary>
    public static string Scrub(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        var scrubbed = PathPattern().Replace(s, Replacement);
        return scrubbed.Length > MaxLength ? scrubbed[..MaxLength] : scrubbed;
    }
}
