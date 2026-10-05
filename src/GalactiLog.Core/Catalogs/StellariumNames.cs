using System.Text;
using System.Text.RegularExpressions;
using GalactiLog.Core.Io;

namespace GalactiLog.Core.Catalogs;

// Port of stellarium_names.py (design-spec 9.3.2): parses the bundled Stellarium
// names.dat fixed-width file into a lowercased-common-name -> SIMBAD-style-id map.
public static class StellariumNames
{
    private static readonly Regex NameRegex = new(@"_\(""(.+?)""\)", RegexOptions.Compiled);

    // Verbatim PREFIX_MAP from stellarium_names.py. Keys compared case-insensitively; an
    // unmapped prefix passes through unchanged.
    private static readonly Dictionary<string, string> PrefixMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NGC"] = "NGC", ["IC"] = "IC", ["M"] = "M", ["SH2"] = "Sh2-", ["B"] = "Barnard",
        ["CR"] = "Collinder", ["MEL"] = "Melotte", ["LDN"] = "LDN", ["LBN"] = "LBN",
        ["RCW"] = "RCW", ["VDB"] = "vdB", ["CED"] = "Ced", ["GUM"] = "Gum", ["PAL"] = "Palomar",
        ["ST"] = "Stock", ["ACO"] = "ACO", ["PGC"] = "PGC", ["HCG"] = "HCG", ["ARP"] = "Arp",
        ["DWB"] = "DWB", ["SNRG"] = "SNR G",
    };

    // Only "Sh2-" joins with no space; every other mapped prefix joins with one space.
    private static readonly HashSet<string> Hyphenated = new(StringComparer.Ordinal) { "Sh2-" };

    // Fixed-width parse, spec 9.3.2: columns 0-4 prefix, 5-19 object id, 20+ remainder
    // searched for _("name"). Skips: line shorter than 21 chars, blank line, line starting
    // with '#' (after trimming leading whitespace), no regex match, empty prefix or object
    // id after trimming. Common name key is lowercased; first occurrence wins.
    public static IReadOnlyDictionary<string, string> ParseNamesDat(Stream stream)
    {
        var names = new Dictionary<string, string>();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }
            if (line.Length < 21)
            {
                continue;
            }

            var prefix = line[0..5].Trim();
            var objId = line[5..20].Trim();
            var remainder = line[20..];
            var match = NameRegex.Match(remainder);
            if (!match.Success || prefix.Length == 0 || objId.Length == 0)
            {
                continue;
            }

            var commonName = match.Groups[1].Value.ToLowerInvariant();
            if (!names.ContainsKey(commonName))
            {
                names[commonName] = ToSimbadId(prefix, objId);
            }
        }
        return names;
    }

    private static string ToSimbadId(string prefix, string objId)
    {
        var mapped = PrefixMap.TryGetValue(prefix, out var m) ? m : prefix;
        return Hyphenated.Contains(mapped) ? $"{mapped}{objId}" : $"{mapped} {objId}";
    }

    // Load-once cache, mirroring stellarium_names.py's module-level `_cache` global exactly:
    // no lock. Safe in practice because every caller in this port (the offline catalog
    // lookup, invoked from the single scan writer task per spec 10.6, and the CLI's
    // single-threaded `resolve` verb) reaches this from one thread at a time; a genuine
    // first-call race would at worst parse the file twice and agree on the result, never
    // corrupt it.
    private static IReadOnlyDictionary<string, string>? _cache;

    public static IReadOnlyDictionary<string, string> GetNames(string catalogsDirectory)
    {
        if (_cache is not null)
        {
            return _cache;
        }
        using var stream = UserFiles.OpenRead(Path.Combine(catalogsDirectory, "names.dat"));
        _cache = ParseNamesDat(stream);
        return _cache;
    }
}
