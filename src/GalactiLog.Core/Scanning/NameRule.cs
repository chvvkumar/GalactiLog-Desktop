using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace GalactiLog.Core.Scanning;

// Port of backend/app/services/scan_filters.py's NameRule dataclass (design-spec 10.2).
// Action/type/target stay plain strings, not enums: they round-trip through the
// general.scan_filters JSON column untouched, and validity is a Validate()-time concern
// (ScanFilterConfig.Validate), not a type-system one.
public sealed record NameRule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("action")] public string Action { get; init; } = "";   // "include" | "exclude"
    [JsonPropertyName("type")] public string Type { get; init; } = "";       // "glob" | "substring" | "regex"
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("target")] public string Target { get; init; } = "";   // "file" | "folder"
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

// Static matcher holding the compiled-regex cache. NameRule itself stays plain data (no
// regex compilation) so `with`-copies and record equality keep working.
public static class NameRuleMatcher
{
    // Keyed by (Type, Pattern) so a glob and a regex sharing the same literal pattern text
    // never collide (review escalation 1). A rule is revalidated once via
    // Validate/EnsureCompilable; after that its pattern is known-good and reused across
    // every Matches() call for the life of the process.
    private static readonly ConcurrentDictionary<(string Type, string Pattern), Regex> RegexCache = new();

    // Review item 5: surfaces a regex/glob match timeout instead of only swallowing it.
    // TimeoutObserved increments on every timeout, unconditionally. OnTimeout fires once
    // per distinct (Type, Pattern) per process -- set once by the scan coordinator (a later
    // task) to log or emit an activity event; left null in every context that doesn't
    // subscribe (including every test but the one exercising this).
    public static int TimeoutObserved;
    public static Action<NameRule>? OnTimeout;
    private static readonly ConcurrentDictionary<(string Type, string Pattern), byte> NotifiedTimeouts = new();

    public static bool Matches(NameRule rule, string value)
    {
        if (!rule.Enabled) return false;
        return rule.Type switch
        {
            "glob" => TryMatch(rule, value, GlobToRegexPattern),
            "substring" => value.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            "regex" => TryMatch(rule, value, static p => p),
            _ => false,
        };
    }

    // Deliberate divergence from scan_filters.py (spec 10.2's called-out divergence): this
    // always checks Enabled first, for EVERY caller including the include-narrowing
    // selection in ScanFilterConfig.ShouldIncludeFile. Python's should_include_file selects
    // include rules by action/target directly and never calls matches() on them for the
    // narrowing step, so a disabled include rule still narrows there. This port always
    // routes through Matches(), so a disabled rule -- include or exclude -- is a no-op
    // everywhere.
    //
    // Shared by the "regex" and "glob" cases (a glob is just a regex built from a
    // translated pattern). Two failure modes, both survivable rather than fatal to a scan:
    // - Review escalation 2: an uncompilable pattern (should have been caught by
    //   Validate, but this is defense-in-depth for a rule that reached matching
    //   unvalidated) degrades to "never matches" instead of throwing out of the walk.
    // - A catastrophic pattern hits the 250 ms match timeout; degrades to "never matches"
    //   and is surfaced via TimeoutObserved/OnTimeout above.
    private static bool TryMatch(NameRule rule, string value, Func<string, string> toRegexPattern)
    {
        Regex regex;
        try
        {
            regex = RegexCache.GetOrAdd((rule.Type, rule.Pattern),
                key => new Regex(toRegexPattern(key.Pattern), RegexOptions.None, TimeSpan.FromMilliseconds(250)));
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            ReportTimeout(rule);
            return false;
        }
    }

    private static void ReportTimeout(NameRule rule)
    {
        Interlocked.Increment(ref TimeoutObserved);
        if (NotifiedTimeouts.TryAdd((rule.Type, rule.Pattern), 0))
        {
            OnTimeout?.Invoke(rule);
        }
    }

    // fnmatch.fnmatchcase semantics, ported from CPython's fnmatch.translate: '*' -> any
    // run of chars, '?' -> any one char, '[seq]'/'[!seq]' -> character class, everything
    // else escaped literally. Anchored so the match is whole-string, matching
    // fnmatchcase's behavior.
    private static string GlobToRegexPattern(string pattern)
    {
        var sb = new StringBuilder();
        var i = 0;
        var n = pattern.Length;
        while (i < n)
        {
            var c = pattern[i++];
            if (c == '*')
            {
                sb.Append(".*");
            }
            else if (c == '?')
            {
                sb.Append('.');
            }
            else if (c == '[')
            {
                var j = i;
                if (j < n && pattern[j] == '!') j++;
                if (j < n && pattern[j] == ']') j++;
                while (j < n && pattern[j] != ']') j++;
                if (j >= n)
                {
                    sb.Append(Regex.Escape("["));
                }
                else
                {
                    var stuff = pattern[i..j];
                    i = j + 1;
                    if (stuff.StartsWith('!')) stuff = "^" + stuff[1..];
                    else if (stuff.StartsWith('^') || stuff.StartsWith('[')) stuff = "\\" + stuff;
                    sb.Append('[').Append(stuff).Append(']');
                }
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return "^" + sb + "$";
    }

    // Triggers pattern compilation for Validate() below, so a bad regex or (in principle) a
    // bad glob-derived pattern surfaces as a validation error rather than at first-match
    // time. Callers translate a resulting ArgumentException (RegexParseException derives
    // from it) into ScanFilterValidationException.
    internal static void EnsureCompilable(NameRule rule)
    {
        if (rule.Type == "regex")
        {
            _ = RegexCache.GetOrAdd((rule.Type, rule.Pattern),
                key => new Regex(key.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)));
        }
        else if (rule.Type == "glob")
        {
            _ = RegexCache.GetOrAdd((rule.Type, rule.Pattern),
                key => new Regex(GlobToRegexPattern(key.Pattern), RegexOptions.None, TimeSpan.FromMilliseconds(250)));
        }
    }
}
