using System.Text.RegularExpressions;

namespace GalactiLog.Core.Targets;

// Port of the similarity() half of PostgreSQL's pg_trgm extension (design-spec 9.7).
// word_similarity() is explicitly out of scope: approximating a scoring function nothing can
// test against would trade a known behavior for an unknown one.
public static partial class Trigram
{
    // pg_trgm splits on ANY run of non-alphanumeric characters, not just whitespace: hyphens,
    // periods, underscores, plus signs all delimit words too ("snr g357.7+00.3" -> "snr",
    // "g357", "7", "00", "3").
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRun();

    // pg_trgm's similarity(a, b): |intersection of 3-gram sets| / |union of 3-gram sets|.
    // Both strings lowercased; each word (split on runs of non-alphanumeric characters) is
    // padded with two leading spaces and one trailing space before 3-grams are taken,
    // matching pg_trgm's own "extended word" padding rule.
    public static double Similarity(string a, string b)
    {
        var setA = TrigramSet(a);
        var setB = TrigramSet(b);

        var union = new HashSet<string>(setA);
        union.UnionWith(setB);
        if (union.Count == 0)
        {
            return 0.0;
        }

        var intersection = new HashSet<string>(setA);
        intersection.IntersectWith(setB);
        return (double)intersection.Count / union.Count;
    }

    private static HashSet<string> TrigramSet(string s)
    {
        var set = new HashSet<string>();
        foreach (var word in NonAlphanumericRun().Split(s.ToLowerInvariant()))
        {
            if (word.Length == 0) continue; // leading/trailing delimiter runs split to ""
            var padded = "  " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                set.Add(padded.Substring(i, 3));
            }
        }
        return set;
    }
}
