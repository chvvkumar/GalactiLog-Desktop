using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;

namespace GalactiLog.Core.Aliases;

/// <summary>
/// Canonical name expansion for optical filters, cameras and telescopes (spec 5.8.4, 12.2).
/// Immutable once built; rebuild it when the settings documents change.
/// </summary>
public sealed class AliasMap
{
    private readonly IReadOnlyList<string> _configuredFilters;
    private readonly IReadOnlyList<string> _configuredCameras;
    private readonly IReadOnlyList<string> _configuredTelescopes;

    private readonly Dictionary<string, string[]> _filterForward;
    private readonly Dictionary<string, string[]> _cameraForward;
    private readonly Dictionary<string, string[]> _telescopeForward;

    private readonly Dictionary<string, string> _filterReverse;
    private readonly Dictionary<string, string> _cameraReverse;
    private readonly Dictionary<string, string> _telescopeReverse;

    // Null is a configured filter with no stored colour (P13 R2a), which is what lets the seeded
    // palette win; blank is treated the same way in FilterColor below.
    private readonly Dictionary<string, string?> _filterColors;

    public AliasMap(IReadOnlyDictionary<string, FilterSetting> filters, EquipmentSettings equipment)
    {
        // Dedupe once, by normalized canonical key, first-configured-wins, before any of the
        // four structures (configured list, forward index, reverse index, colors) are built,
        // so a canonical-key collision (blank, or two keys differing only by case/whitespace)
        // cannot desync them or throw. Review fix item 1.
        var filterEntries = DedupeCanonicals(filters);
        (_configuredFilters, _filterForward, _filterReverse) =
            Build(filterEntries.Select(e => (e.Canonical, e.Value.Aliases)));
        _filterColors = filterEntries.ToDictionary(
            e => NameNormalizer.Normalize(e.Canonical),
            e => e.Value.Color);

        var cameraEntries = DedupeCanonicals(equipment.Cameras);
        (_configuredCameras, _cameraForward, _cameraReverse) =
            Build(cameraEntries.Select(e => (e.Canonical, e.Value.Aliases)));

        var telescopeEntries = DedupeCanonicals(equipment.Telescopes);
        (_configuredTelescopes, _telescopeForward, _telescopeReverse) =
            Build(telescopeEntries.Select(e => (e.Canonical, e.Value.Aliases)));
    }

    /// <summary>Canonical filter names configured by the user, ordered as configured.</summary>
    public IReadOnlyList<string> ConfiguredFilters => _configuredFilters;
    public IReadOnlyList<string> ConfiguredCameras => _configuredCameras;
    public IReadOnlyList<string> ConfiguredTelescopes => _configuredTelescopes;

    /// <summary>The forward expansion: canonical name to every raw name that maps to it,
    /// canonical name included, deduplicated case-insensitively, canonical name first. An
    /// unconfigured canonical name expands to a single-element list holding itself.</summary>
    public IReadOnlyList<string> ExpandFilter(string canonical) => Expand(_filterForward, canonical);
    public IReadOnlyList<string> ExpandCamera(string canonical) => Expand(_cameraForward, canonical);
    public IReadOnlyList<string> ExpandTelescope(string canonical) => Expand(_telescopeForward, canonical);

    /// <summary>Every spelling a raw telescope name answers to, as the case-insensitive set a
    /// rig comparison is made against: the raw name is folded to its canonical form first, then
    /// expanded to that canonical name plus every configured alias.
    /// <para>
    /// The fold happens INSIDE this member on purpose, which is the reason the web's
    /// <c>load_telescope_match_set</c> gives in its own docstring: a caller handed only
    /// <see cref="ExpandTelescope"/> has to remember the
    /// <c>CanonicalTelescope(x) ?? x</c> step, and the caller that forgets it silently
    /// reintroduces the raw comparison. That comparison missed a night shot on
    /// "SVBony SV503 80mm" whose profile was mapped to "SVBony 80ED"; both sides are user data
    /// written at different times. The two callers are the Guiding band's night query and the
    /// correlation pass, which is design lesson 1's second occurrence (phase-review.md F7).
    /// </para></summary>
    public IReadOnlySet<string> TelescopeMatchSet(string raw)
        => new HashSet<string>(
            ExpandTelescope(CanonicalTelescope(raw) ?? raw), StringComparer.OrdinalIgnoreCase);

    /// <summary>The reverse fold: a raw stored name to its canonical name, or the raw name
    /// itself when it is in no alias list. Null or whitespace returns null.</summary>
    public string? CanonicalFilter(string? raw) => Canonical(_filterReverse, raw);
    public string? CanonicalCamera(string? raw) => Canonical(_cameraReverse, raw);
    public string? CanonicalTelescope(string? raw) => Canonical(_telescopeReverse, raw);

    /// <summary>
    /// The configured or seeded colour for a canonical filter (HANDOFF 5.2 item 12, P13 R2). The
    /// order lives in one place for the whole application,
    /// <c>GalactiLog.Core.Aliases.FilterColor.Resolve</c>, and this method is the entry point
    /// every consuming surface reaches it through; the Settings swatch reaches the same method
    /// through <c>AliasGroupViewModel.ResolvedColor</c>. In order: the stored colour for this
    /// canonical name, when it carries intent; the seeded palette entry for the category the
    /// canonical name folds to; the seeded palette entry for the category the first of its
    /// configured aliases folds to, in <see cref="ExpandFilter"/> order; spec 5.8.4's grey.
    /// </summary>
    /// <remarks>
    /// A stored <c>#808080</c> is treated as no stored colour at all, because every build before
    /// Phase 13 wrote that exact string into every group it created without the user asking
    /// (review P3-5); <c>FilterColor.AsStored</c> holds that rule and its consequence.
    /// </remarks>
    public string FilterColor(string canonical)
    {
        _filterColors.TryGetValue(NameNormalizer.Normalize(canonical), out var stored);

        // ExpandFilter already puts the canonical name first, so the alias sweep inside Resolve
        // also covers the canonical's own category; passing both is what keeps the fast path and
        // the loop from ever being two different answers.
        return global::GalactiLog.Core.Aliases.FilterColor.Resolve(
            stored, canonical, ExpandFilter(canonical));
    }

    private static IReadOnlyList<string> Expand(Dictionary<string, string[]> forward, string canonical)
        => forward.TryGetValue(NameNormalizer.Normalize(canonical), out var expansion) ? expansion : [canonical];

    private static string? Canonical(Dictionary<string, string> reverse, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return reverse.TryGetValue(NameNormalizer.Normalize(raw), out var canonical) ? canonical : raw;
    }

    // De-dupes the configured entries by normalized canonical key, first-configured-wins, and
    // drops a null/blank canonical entirely. The single choke point every one of AliasMap's
    // four derived structures is built from, so they can never disagree about which canonical
    // survived a collision. Review fix item 1.
    private static List<(string Canonical, TValue Value)> DedupeCanonicals<TValue>(IEnumerable<KeyValuePair<string, TValue>> source)
    {
        var result = new List<(string, TValue)>();
        var seen = new HashSet<string>();
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (seen.Add(NameNormalizer.Normalize(key))) result.Add((key, value));
        }
        return result;
    }

    // Builds the ordered configured-name list, the forward (canonical -> expansion) index and
    // the reverse (raw -> canonical) index for one namespace (filters, cameras or telescopes)
    // in a single pass over the already-deduped configured entries.
    //
    // Alias collision -- the same raw name listed under two canonical names -- is resolved
    // first-configured-wins in the reverse index, and is not an error here; the Settings screen
    // (Phase 9) is where a user is told about it, not this type.
    private static (IReadOnlyList<string> Configured, Dictionary<string, string[]> Forward, Dictionary<string, string> Reverse)
        Build(IEnumerable<(string Canonical, string[] Aliases)> entries)
    {
        var configured = new List<string>();
        var forward = new Dictionary<string, string[]>();
        var reverse = new Dictionary<string, string>();

        foreach (var (canonical, aliases) in entries)
        {
            configured.Add(canonical);

            var seen = new HashSet<string>();
            var expansion = new List<string>();
            // Null/blank aliases are skipped (review fix item 2): a null would throw in
            // Normalize, and a blank would otherwise land in the expansion list and match
            // filter_used = '' -- the value Q14 reserves for the synthesized "Unknown" bucket.
            void AddIfNew(string? display)
            {
                if (string.IsNullOrWhiteSpace(display)) return;
                if (seen.Add(NameNormalizer.Normalize(display))) expansion.Add(display);
            }
            AddIfNew(canonical);
            foreach (var alias in aliases) AddIfNew(alias);

            forward[NameNormalizer.Normalize(canonical)] = [.. expansion];

            foreach (var display in expansion)
            {
                reverse.TryAdd(NameNormalizer.Normalize(display), canonical);
            }
        }

        return (configured, forward, reverse);
    }
}
