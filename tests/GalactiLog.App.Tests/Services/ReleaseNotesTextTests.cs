using GalactiLog.App.Services;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Spec 19.2: the release notes are rendered as text, so the markdown the feed carries is
/// reduced to plain text before any view sees it.
/// </summary>
public class ReleaseNotesTextTests
{
    [Fact]
    public void Plain_StripsTheMarkupTheReleaseScriptEmits()
    {
        const string markdown =
            "This update clarifies language.\n" +
            "\n" +
            "## Changes since 1.0.1-alpha.2\n" +
            "- Updated help text to use 'nights list' (3df656b)\n" +
            "- Rewrote the `README` for [clearer](https://example.test/x) language (c63a591)\n" +
            "\n" +
            "**Full changelog**: https://github.com/o/r/compare/1.0.1-alpha.2...1.0.1-alpha.3\n";

        const string expected =
            "This update clarifies language.\n" +
            "\n" +
            "Changes since 1.0.1-alpha.2\n" +
            "- Updated help text to use 'nights list' (3df656b)\n" +
            "- Rewrote the README for clearer language (c63a591)\n" +
            "\n" +
            "Full changelog: https://github.com/o/r/compare/1.0.1-alpha.2...1.0.1-alpha.3";

        Assert.Equal(expected, ReleaseNotesText.Plain(markdown));
    }

    [Fact]
    public void Plain_LeavesPlainTextAlone()
    {
        Assert.Equal("Fixed the thing", ReleaseNotesText.Plain("Fixed the thing"));
        Assert.Equal("a * b and a_b", ReleaseNotesText.Plain("a * b and a_b"));
    }
}
