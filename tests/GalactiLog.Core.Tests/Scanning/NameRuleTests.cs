using GalactiLog.Core.Scanning;
using Xunit;

namespace GalactiLog.Core.Tests.Scanning;

public class NameRuleTests
{
    private static NameRule Rule(string type, string pattern, bool enabled = true, string id = "r1") => new()
    {
        Id = id,
        Action = "exclude",
        Type = type,
        Pattern = pattern,
        Target = "file",
        Enabled = enabled,
    };

    [Theory]
    [InlineData("cal_*.fits", "cal_bias.fits", true)]
    [InlineData("cal_*.fits", "CAL_BIAS.FITS", false)] // case-sensitive
    [InlineData("cal_*.fits", "light_cal_bias.fits", false)] // full-string, not containment
    [InlineData("m3?.fits", "m31.fits", true)]
    [InlineData("m3?.fits", "m3.fits", false)]
    public void Matches_Glob_IsCaseSensitiveFullString(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, NameRuleMatcher.Matches(Rule("glob", pattern), value));
    }

    [Theory]
    [InlineData("WORK_AREA", "C:\\Astro\\WORK_AREA", true)]
    [InlineData("WORK_AREA", "C:\\Astro\\work_area", true)] // case-insensitive
    [InlineData("WORK_AREA", "C:\\Astro\\Calibrated", false)]
    public void Matches_Substring_IsCaseInsensitiveContainment(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, NameRuleMatcher.Matches(Rule("substring", pattern), value));
    }

    [Theory]
    [InlineData(@"cal_", "cal_bias.fits", true)]
    [InlineData(@"cal_", "dark_cal_bias.fits", true)] // unanchored search, matches mid-string
    [InlineData(@"^cal_", "dark_cal_bias.fits", false)] // explicit anchor still honored
    [InlineData(@"\.tmp$", "frame.fits", false)]
    public void Matches_Regex_IsUnanchoredSearch(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, NameRuleMatcher.Matches(Rule("regex", pattern), value));
    }

    [Theory]
    [InlineData("glob", "*.fits")]
    [InlineData("substring", "cal")]
    [InlineData("regex", ".*")]
    public void Matches_DisabledRule_NeverMatches(string type, string pattern)
    {
        var rule = Rule(type, pattern, enabled: false);

        Assert.False(NameRuleMatcher.Matches(rule, "cal_bias.fits"));
    }

    [Fact]
    public void Matches_CatastrophicRegex_TimesOutAndReturnsFalse()
    {
        var rule = Rule("regex", "(a+)+$");
        var value = new string('a', 40) + "!";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = NameRuleMatcher.Matches(rule, value);
        sw.Stop();

        Assert.False(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    // Review item 5: a timeout must not just be swallowed silently. Uses a pattern/id
    // unused by any other test in this file so the process-wide "notified once" state
    // (keyed by (Type, Pattern)) starts fresh regardless of test execution order.
    [Fact]
    public void Matches_CatastrophicRegex_SurfacesTimeoutOnceViaCallback_CounterEveryTime()
    {
        var rule = Rule("regex", "(c+)+$", id: "catastrophic-review-item-5");
        var value = new string('c', 40) + "!";
        var callbackCount = 0;
        NameRule? notifiedRule = null;
        NameRuleMatcher.OnTimeout = r => { callbackCount++; notifiedRule = r; };
        try
        {
            var before = NameRuleMatcher.TimeoutObserved;

            NameRuleMatcher.Matches(rule, value);
            NameRuleMatcher.Matches(rule, value);

            Assert.Equal(before + 2, NameRuleMatcher.TimeoutObserved); // counter: every time
            Assert.Equal(1, callbackCount); // callback: first time per rule per process
            Assert.Equal("catastrophic-review-item-5", notifiedRule?.Id);
        }
        finally
        {
            NameRuleMatcher.OnTimeout = null;
        }
    }

    // Review escalation 2: an uncompilable pattern must degrade to "never matches" rather
    // than throw out of a scan. Validate() is the real gate; this is defense-in-depth for a
    // rule that somehow reached matching unvalidated (e.g. persisted before Validate was
    // wired in, or a future caller that skips it).
    [Fact]
    public void Matches_UncompilableRegexPattern_DegradesToNoMatch()
    {
        var rule = Rule("regex", "(unterminated", id: "uncompilable-regex");

        var result = Record.Exception(() => NameRuleMatcher.Matches(rule, "anything.fits"));

        Assert.Null(result);
        Assert.False(NameRuleMatcher.Matches(rule, "anything.fits"));
    }
}
