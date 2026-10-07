using System.Text.RegularExpressions;

namespace GalactiLog.App.Services;

/// <summary>
/// Reduces the markdown the update feed carries to plain text. Spec 19.2: no embedded browser,
/// the notes are rendered in a text block, so the markup must go before a view binds them.
/// </summary>
/// <remarks>
/// Covers what the release script and its model emit: ATX headings, bold and italic runs,
/// inline code, links. Bullets stay as written; a leading dash reads fine as text.
/// </remarks>
internal static partial class ReleaseNotesText
{
    public static string Plain(string markdown)
    {
        var text = Heading().Replace(markdown, "");
        text = Link().Replace(text, "$1");
        text = Emphasis().Replace(text, "$2");
        text = Code().Replace(text, "$1");
        return text.Trim();
    }

    [GeneratedRegex(@"^#{1,6}[ \t]+", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    // ** or * or __ or _ wrapping a run with no spaces at its edges; "a * b" and "a_b" are untouched.
    [GeneratedRegex(@"(\*\*|__|\*|_)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"`([^`]*)`")]
    private static partial Regex Code();
}
