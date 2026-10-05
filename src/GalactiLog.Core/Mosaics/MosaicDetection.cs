using System.Globalization;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Sessions;

namespace GalactiLog.Core.Mosaics;

/// <summary>
/// Mosaic detection of spec 7.7 as pure functions over records: candidates, grouping, scoring,
/// campaigns, names, the dedup signature and the skip rules. Port of <c>mosaic_detection.py</c>
/// amended by rulings R4 to R6, R10, R19 and R19a. The pass that reads the catalogue and writes
/// the result is <c>GalactiLog.Data.Ingest.MosaicDetectionPass</c>.
/// </summary>
public static class MosaicDetection
{
    /// <summary>The web's <c>DEFAULT_TOLERANCE_ARCMIN</c> (spec 7.7, the tolerance).</summary>
    public const double DefaultToleranceArcmin = 12.0;

    private static readonly StringComparer NoCase = StringComparer.OrdinalIgnoreCase;

    private const string NotDistinctFlag =
        "Positions not distinct: two panels share a sky centre within tolerance (possible duplicate or mislabel).";
    private const string UnrelatedFlag = "Position-grouped targets have unrelated base names.";
    private const string NoPositionFlag = "Some panels have no usable sky coordinates.";
    private const string IrregularFlag = "Panel spread is far larger than the field of view (irregular geometry).";
    private const string MixedScalesFlag = "Mixed plate scales across panels.";
    private const string MixedKeywordsFlag = "Panels name their number with different keywords.";
    private const string OnePanelFlag = "Only one panel found.";

    private sealed record Centre(double Ra, double Dec);

    private sealed record Candidate(
        Guid TargetId, string BaseName, string? Keyword, string Number, Centre? Centre, double? Fov,
        IReadOnlyList<DateOnly> Nights)
    {
        public string Label => PanelTokens.Label(Number);
    }

    private sealed record Group(
        IReadOnlyList<Candidate> Entries, string Source, string Confidence, IReadOnlyList<string> Flags,
        double? Fov, bool OnePanel)
    {
        public string BaseName => Entries[0].BaseName;
    }

    /// <summary>The robust centre (spec 7.7, port of <c>robust_median_center</c>) over the frames
    /// with both a stored right ascension and declination; null when none has.</summary>
    public static (double RaDeg, double DecDeg)? RobustCentre(IEnumerable<DetectionFrame> frames)
    {
        var coords = frames.Where(f => f.RaDeg is not null && f.DecDeg is not null)
            .Select(f => (Ra: f.RaDeg!.Value, Dec: f.DecDeg!.Value)).ToList();
        if (coords.Count == 0) return null;

        var dec = Statistics.Median(coords.Select(c => c.Dec))!.Value;
        var cos = Math.Cos(dec * Math.PI / 180);
        if (cos == 0) cos = 1e-9;
        var reference = Statistics.Median(coords.Select(c => c.Ra))!.Value;
        var offset = Statistics.Median(coords.Select(c =>
            (AstroNight.Mod360(c.Ra - reference + 180) - 180) * cos))!.Value;
        return (AstroNight.Mod360(reference + offset / cos), dec);
    }

    /// <summary><c>width_px * arcsec_per_pixel / 60</c> in arcminutes, or null when either is
    /// missing or not positive (spec 7.7, the field of view).</summary>
    public static double? FieldOfViewArcmin(double? arcsecPerPixel, int? widthPx) =>
        arcsecPerPixel is > 0 && widthPx is > 0 ? widthPx.Value * arcsecPerPixel.Value / 60 : null;

    /// <summary>Haversine great-circle separation in degrees (spec 7.7, separation).</summary>
    public static double AngularSeparationDeg(double ra1, double dec1, double ra2, double dec2)
    {
        const double rad = Math.PI / 180;
        var a = Math.Pow(Math.Sin((dec2 - dec1) * rad / 2), 2)
            + Math.Cos(dec1 * rad) * Math.Cos(dec2 * rad) * Math.Pow(Math.Sin((ra2 - ra1) * rad / 2), 2);
        return 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1))) / rad;
    }

    /// <summary>The run's tolerance (spec 7.7): the configured value when positive, else a quarter
    /// of the smallest field of view but at least 1.0, else <see cref="DefaultToleranceArcmin"/>.</summary>
    public static double EffectiveToleranceArcmin(double configuredArcmin, IEnumerable<double?> fieldsOfViewArcmin)
    {
        if (configuredArcmin > 0) return configuredArcmin;
        var fovs = fieldsOfViewArcmin.Where(f => f is > 0).Select(f => f!.Value).ToList();
        return fovs.Count > 0 ? Math.Max(0.25 * fovs.Min(), 1.0) : DefaultToleranceArcmin;
    }

    /// <summary>The distinct nights, sorted, split wherever two consecutive nights are more than
    /// <paramref name="gapDays"/> apart (spec 7.7, campaigns). Port of
    /// <c>cluster_sessions_by_gap</c>.</summary>
    public static IReadOnlyList<IReadOnlyList<DateOnly>> ClusterByGap(IEnumerable<DateOnly> dates, int gapDays)
    {
        var clusters = new List<List<DateOnly>>();
        foreach (var d in dates.Distinct().Order())
        {
            if (clusters.Count == 0 || d.DayNumber - clusters[^1][^1].DayNumber > gapDays) clusters.Add([]);
            clusters[^1].Add(d);
        }
        return clusters;
    }

    /// <summary><c>base|target ids|labels</c>, ids and labels each sorted ordinally and joined
    /// with ',', duplicates kept (spec 7.7, the dedup signature). Port of
    /// <c>compute_dedup_signature</c>.</summary>
    public static string DedupSignature(string baseName, IEnumerable<Guid> targetIds, IEnumerable<string> panelLabels) =>
        string.Join('|', baseName,
            string.Join(',', targetIds.Select(t => t.ToString()).Order(StringComparer.Ordinal)),
            string.Join(',', panelLabels.Order(StringComparer.Ordinal)));

    /// <summary>
    /// The whole of spec 7.7 from candidates to the suggestions a run inserts: grouping, scoring,
    /// campaign split, the skip rules of "Writing suggestions" step 4 in their order (existing
    /// mosaic name, covered triples, dismissed subset) and unique names, applied in emission order.
    /// The result is ordered by suggested name.
    /// </summary>
    /// <param name="targets">Unmerged targets with their LIGHT frames.</param>
    /// <param name="existingMosaicNames">Every mosaic's name; compared case insensitively.</param>
    /// <param name="dismissed">Rejected signatures with their pooled nights.</param>
    /// <param name="coveredTriples">Every (target, night, frame label) an <c>included</c> row
    /// holds; null for none.</param>
    public static IReadOnlyList<SuggestionCandidate> Detect(
        IReadOnlyList<DetectionTarget> targets, DetectionSettings settings,
        IReadOnlySet<string> existingMosaicNames, IReadOnlyList<DismissedSignature> dismissed,
        IReadOnlySet<(Guid TargetId, DateOnly Night, string Label)>? coveredTriples = null)
    {
        var candidates = BuildCandidates(targets, settings.Keywords);
        if (candidates.Count == 0) return [];
        var tolerance = EffectiveToleranceArcmin(settings.PositionToleranceArcmin, candidates.Select(c => c.Fov));

        var dismissedNights = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
        foreach (var d in dismissed)
        {
            if (!dismissedNights.TryGetValue(d.Signature, out var set)) dismissedNights[d.Signature] = set = [];
            set.UnionWith(d.Dates);
        }
        var existing = new HashSet<string>(existingMosaicNames, NoCase);
        var used = new HashSet<string>(existingMosaicNames, NoCase);
        var result = new List<SuggestionCandidate>();

        foreach (var group in GroupCandidates(candidates, tolerance))
        {
            foreach (var (name, entries) in Campaigns(group, settings.CampaignGapDays))
            {
                if (existing.Contains(name)) continue;
                var triples = entries.SelectMany(e => e.Nights.Select(n => (e.TargetId, n, e.Label))).ToList();
                if (coveredTriples is not null && triples.Count > 0 && triples.All(coveredTriples.Contains)) continue;
                var signature = DedupSignature(group.BaseName, entries.Select(e => e.TargetId), entries.Select(e => e.Label));
                if (dismissedNights.TryGetValue(signature, out var dismissedSet)
                    && triples.All(t => dismissedSet.Contains(t.n))) continue;

                result.Add(new SuggestionCandidate(
                    UniqueName(name, used), group.BaseName,
                    [.. entries.Select(e => new SuggestionPanel(
                        e.TargetId, e.Label, PanelTokens.BuildPattern(e.BaseName, e.Keyword, e.Number), e.Nights))],
                    group.Confidence, group.Source, Geometry(entries, group.Fov), group.Flags, signature));
            }
        }
        return [.. result.OrderBy(s => s.SuggestedName, NoCase).ThenBy(s => s.SuggestedName, StringComparer.Ordinal)];
    }

    // Spec 7.7, candidates: one per distinct token OBJECT of a target, targets by id and OBJECT
    // strings ordinally; a later OBJECT of the same base, compared case insensitively, and number
    // yields none.
    private static List<Candidate> BuildCandidates(IReadOnlyList<DetectionTarget> targets, IReadOnlyList<string> keywords)
    {
        var candidates = new List<Candidate>();
        foreach (var target in targets.OrderBy(t => t.Id.ToString(), StringComparer.Ordinal))
        {
            var seen = new HashSet<(string, string)>();
            var names = target.Frames.Select(f => f.ObjectName).OfType<string>().Distinct().Order(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (PanelTokens.Match(name, keywords) is not { } m || !seen.Add((m.BaseName.ToUpperInvariant(), m.Number))) continue;
                // The frames carrying this OBJECT, so the centre is per OBJECT (ruling R19a).
                var frames = target.Frames.Where(f => f.ObjectName == name).ToList();
                var centre = RobustCentre(frames) is { } c ? new Centre(c.RaDeg, c.DecDeg) : null;
                var fov = frames.Select(f => FieldOfViewArcmin(f.ArcsecPerPixel, f.WidthPx)).FirstOrDefault(v => v is not null);
                var nights = frames.Where(f => f.SessionDate is not null).Select(f => f.SessionDate!.Value).Distinct().Order().ToList();
                candidates.Add(new Candidate(target.Id, m.BaseName, m.Keyword, m.Number, centre, fov, nights));
            }
        }
        return candidates;
    }

    // Spec 7.7, grouping: by name, then by position, then one-panel groups; each set ordered by
    // base name, ordinal case insensitive.
    private static List<Group> GroupCandidates(List<Candidate> candidates, double tolerance)
    {
        var nameGroups = new List<Group>();
        var consumed = new HashSet<Guid>();
        foreach (var members in candidates.GroupBy(c => c.BaseName, NoCase))
        {
            var unique = members.DistinctBy(m => m.Number).ToList();
            if (unique.Count < 2) continue;
            nameGroups.Add(Finalise(unique, Distinct(unique, tolerance) ? "both" : "name", tolerance, crossName: false));
            consumed.UnionWith(unique.Select(m => m.TargetId));
        }

        var positionGroups = new List<Group>();
        var remaining = candidates.Where(c => !consumed.Contains(c.TargetId) && c.Centre is not null).ToList();
        foreach (var cluster in ClusterByPosition(remaining, tolerance))
        {
            if (cluster.Select(c => c.TargetId).Distinct().Count() < 2 || cluster.Select(c => c.Number).Distinct().Count() < 2) continue;
            var crossName = cluster.Select(c => c.BaseName).Distinct(NoCase).Count() > 1;
            positionGroups.Add(Finalise(cluster, "position", tolerance, crossName));
        }

        // One-panel groups only for bases with no entry in any group; a leftover candidate of a
        // grouped base is dropped, as the web drops it.
        var groupedEntries = nameGroups.Concat(positionGroups).SelectMany(g => g.Entries).ToList();
        var groupedTargets = groupedEntries.Select(e => e.TargetId).ToHashSet();
        var groupedBases = groupedEntries.Select(e => e.BaseName).ToHashSet(NoCase);
        var onePanelGroups = candidates
            .Where(c => !groupedTargets.Contains(c.TargetId) && !groupedBases.Contains(c.BaseName))
            .GroupBy(c => c.BaseName, NoCase)
            .Select(members => Finalise([.. members], "name", tolerance, crossName: false)).ToList();

        return [.. ByBase(nameGroups), .. ByBase(positionGroups), .. ByBase(onePanelGroups)];

        static IEnumerable<Group> ByBase(List<Group> groups) => groups.OrderBy(g => g.BaseName, NoCase);
    }

    // Single link within the reach: three median fields of view, or ten tolerances (spec 7.7).
    // ponytail: O(n^2) pair scan, fine for a catalogue's panel candidates; a spatial index if a
    // measurement ever asks.
    private static List<List<Candidate>> ClusterByPosition(List<Candidate> candidates, double tolerance)
    {
        var reach = MedianFov(candidates) is { } fov ? fov * 3 : tolerance * 10;
        var parent = Enumerable.Range(0, candidates.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < candidates.Count; i++)
            for (var j = i + 1; j < candidates.Count; j++)
                if (SeparationArcmin(candidates[i].Centre!, candidates[j].Centre!) <= reach) parent[Find(i)] = Find(j);
        return [.. Enumerable.Range(0, candidates.Count).GroupBy(Find).Select(g => g.Select(i => candidates[i]).ToList())];
    }

    // Entries sorted by panel number (stable), then the checks of spec 7.7's flag table in order.
    private static Group Finalise(List<Candidate> members, string source, double tolerance, bool crossName)
    {
        var entries = members.OrderBy(m => m.Number, PanelNumberComparer.Instance).ToList();
        var pitches = Pitches(entries);
        var fovs = entries.Where(e => e.Fov is > 0).Select(e => e.Fov!.Value).ToList();
        var medianFov = Statistics.Median(fovs);
        var onePanel = entries.Select(e => e.Number).Distinct().Count() == 1;

        var flags = new List<string>();
        if (pitches.Any(p => p < tolerance)) flags.Add(NotDistinctFlag);
        if (crossName) flags.Add(UnrelatedFlag);
        if (entries.Any(e => e.Centre is null)) flags.Add(NoPositionFlag);
        if (entries.Count(e => e.Centre is not null) >= 3 && medianFov is { } med && pitches.Max() > 8 * med) flags.Add(IrregularFlag);
        if (fovs.Count >= 2 && fovs.Max() > 1.5 * fovs.Min()) flags.Add(MixedScalesFlag);
        if (entries.Select(e => e.Keyword ?? "\0tile").Distinct(NoCase).Count() > 1) flags.Add(MixedKeywordsFlag);
        if (onePanel) flags.Add(OnePanelFlag);

        var confidence = flags.Count == 0 && source == "both" ? "high" : "low";
        return new Group(entries, source, confidence, flags,
            medianFov is { } m ? Math.Round(m, 2) : null, onePanel);
    }

    // Spec 7.7, campaigns and names: one suggestion over every night unless the gap splits the
    // nights into two or more campaigns.
    private static IEnumerable<(string Name, List<Candidate> Entries)> Campaigns(Group group, int gapDays)
    {
        IReadOnlyList<IReadOnlyList<DateOnly>> clusters = gapDays > 0 ? ClusterByGap(group.Entries.SelectMany(e => e.Nights), gapDays) : [];
        if (clusters.Count <= 1)
        {
            yield return (group.BaseName, [.. group.Entries]);
            yield break;
        }
        foreach (var cluster in clusters)
        {
            var nights = cluster.ToHashSet();
            var entries = group.Entries.Where(e => e.Nights.Any(nights.Contains))
                .Select(e => e with { Nights = [.. e.Nights.Where(nights.Contains)] }).ToList();
            if (!group.OnePanel && entries.Select(e => e.Number).Distinct().Count() < 2) continue;
            yield return ($"{group.BaseName} {DateRangeSuffix(cluster[0], cluster[^1])}", entries);
        }
    }

    /// <summary>The date range suffix of spec 7.7: <c>(Mar 2026)</c> when both nights fall in one
    /// month, <c>(Mar 2026 - May 2026)</c> otherwise, English month abbreviations whatever the
    /// culture. The Create mosaic dialog's name prefill (spec 12.17) reuses it.</summary>
    public static string DateRangeSuffix(DateOnly first, DateOnly last)
    {
        var a = first.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        var b = last.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        return a == b ? $"({a})" : $"({a} - {b})";
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var chosen = name;
        for (var n = 2; !used.Add(chosen); n++) chosen = $"{name} (#{n})";
        return chosen;
    }

    // Geometry carries the group's field of view unchanged and recomputes pitches over these
    // entries; always built, null ra and dec on an entry without a position (spec 5.25).
    private static SuggestionGeometry Geometry(List<Candidate> entries, double? fov) =>
        new(
            [.. entries.Select(e => new GeometryPanel(e.TargetId, e.Label,
                e.Centre is null ? null : Math.Round(e.Centre.Ra, 6),
                e.Centre is null ? null : Math.Round(e.Centre.Dec, 6)))],
            [.. Pitches(entries).Select(p => Math.Round(p, 3))],
            fov);

    // Separation in arcminutes of every pair of entries with a centre, in pair order.
    private static List<double> Pitches(List<Candidate> entries)
    {
        var located = entries.Where(e => e.Centre is not null).ToList();
        var pitches = new List<double>();
        for (var i = 0; i < located.Count; i++)
            for (var j = i + 1; j < located.Count; j++)
                pitches.Add(SeparationArcmin(located[i].Centre!, located[j].Centre!));
        return pitches;
    }

    private static bool Distinct(List<Candidate> entries, double tolerance) => Pitches(entries).Any(p => p >= tolerance);

    private static double? MedianFov(List<Candidate> candidates) =>
        Statistics.Median(candidates.Where(c => c.Fov is > 0).Select(c => c.Fov!.Value));

    private static double SeparationArcmin(Centre a, Centre b) => AngularSeparationDeg(a.Ra, a.Dec, b.Ra, b.Dec) * 60;

    // The number split on '-' and compared as integers; a number that does not parse sorts after
    // every one that does (the web's _panel_sort_key).
    private sealed class PanelNumberComparer : IComparer<string>
    {
        public static readonly PanelNumberComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (a, b) = (Key(x!), Key(y!));
            if (a is null || b is null) return a is null ? (b is null ? string.CompareOrdinal(x, y) : 1) : -1;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }

        private static long[]? Key(string number)
        {
            var parts = number.Split('-');
            var key = new long[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out key[i])) return null;
            return key;
        }
    }
}
