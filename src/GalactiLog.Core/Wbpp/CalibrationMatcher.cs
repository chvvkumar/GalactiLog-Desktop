using System.Globalization;

namespace GalactiLog.Core.Wbpp;

/// <summary>Selects the flats and flat-darks that match a night's light sets. Hard keys (camera,
/// filter, binning, gain, offset) must agree; soft keys (telescope, rotator within tolerance by
/// shortest arc, readout mode) must agree whenever both sides have them. Among matching flats the
/// same night wins, else the nearest night in either direction. Darks and bias are listed, never
/// selected.</summary>
public static class CalibrationMatcher
{
    public const double RotatorToleranceDegrees = 1.0;
    public const double FlatDarkExposureToleranceSeconds = 0.01;

    public static IReadOnlyList<LightSet> LightSets(IEnumerable<FrameKeys> lights) =>
        lights
            .Select(k => Normalise(k) with { RotatorAngle = k.RotatorAngle is { } r ? Math.Round(r * 10, MidpointRounding.AwayFromZero) / 10 : null })
            .GroupBy(k => (Lower(k.Camera), Lower(k.Filter), k.Binning, k.Gain, k.Offset, Lower(k.Telescope), k.RotatorAngle, Lower(k.ReadoutMode)))
            .Select(g => new LightSet(g.First(), g.Count()))
            .ToList();

    public static bool KeysMatch(FrameKeys light, FrameKeys flat) => FirstMismatch(light, flat) is null;

    public static string NightNote(DateOnly lightNight, DateOnly? frameNight)
    {
        if (frameNight is not { } n) return "night unknown";
        int days = lightNight.DayNumber - n.DayNumber;
        return days switch
        {
            0 => "same night",
            1 => "1 day earlier",
            -1 => "1 day later",
            > 0 => $"{days} days earlier",
            _ => $"{-days} days later",
        };
    }

    public static CalibrationMatch Match(IReadOnlyList<LightSet> lightSets, DateOnly lightNight, IReadOnlyList<ScannedFrame> frames)
    {
        var flats = frames.Where(f => f.Type == CalibrationType.Flat).ToList();
        var selected = new HashSet<ScannedFrame>();
        var warnings = new List<string>();

        foreach (var set in lightSets)
        {
            var picked = Nearest(flats.Where(f => KeysMatch(set.Keys, f.Keys)), lightNight);
            if (picked.Count == 0)
            {
                var k = Normalise(set.Keys);
                var camera = k.Camera ?? "unknown camera";
                var warning = k.Filter is null ? $"No flats found for {camera}, no filter" : $"No flats found for {k.Filter} on {camera}";
                if (!warnings.Contains(warning)) warnings.Add(warning);
            }
            selected.UnionWith(picked);
        }

        foreach (var flat in selected.ToList())
        {
            var darks = frames.Where(f => f.Type == CalibrationType.DarkFlat && DarkFlatFollows(flat, f));
            selected.UnionWith(Nearest(darks, flat.Night ?? lightNight));
        }

        var rows = frames
            .Where(f => f.Type is CalibrationType.Flat or CalibrationType.DarkFlat or CalibrationType.Dark or CalibrationType.Bias)
            .GroupBy(f => Path.GetDirectoryName(f.FilePath) ?? "")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var matches = g
                    .Select(f => new FrameMatch(f, selected.Contains(f), selected.Contains(f) ? NightNote(lightNight, f.Night) : RejectNote(f, lightSets)))
                    .ToList();
                var picked = matches.Where(m => m.Selected).Select(m => m.Note).Distinct().ToList();
                var note = picked.Count > 0 ? string.Join(", ", picked) : matches[0].Note;
                return new CalibrationRow(g.Key, matches, picked.Count > 0, note);
            })
            .ToList();

        return new CalibrationMatch(lightSets, rows, warnings, warnings.Count == 0);
    }

    /// <summary>One line per group of light sets that differ only in filter, for the Calibration found
    /// block: "Light sets: Ha, Oiii on ZWO ASI2600MM Pro, gain 110, offset 50, bin 1, rotation 128.9".</summary>
    public static IReadOnlyList<string> LightSetLines(IReadOnlyList<LightSet> sets)
        => [.. sets
            .GroupBy(set => set.Keys with { Filter = null })
            .Select(group =>
            {
                var keys = group.Key;
                var filters = group.Select(set => set.Keys.Filter).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var camera = keys.Camera ?? "unknown camera";
                var text = "Light sets: " + (filters.Count > 0 ? string.Join(", ", filters) + " on " + camera : camera + ", no filter");
                foreach (var (name, value) in new[] { ("gain", keys.Gain), ("offset", keys.Offset), ("bin", keys.Binning), ("rotation", keys.RotatorAngle) })
                {
                    if (value is { } number)
                    {
                        text += ", " + name + " " + number.ToString("0.##", CultureInfo.InvariantCulture);
                    }
                }

                return text;
            })];

    internal static FrameKeys Normalise(FrameKeys k) => k with
    {
        Camera = Clean(k.Camera),
        Filter = Clean(k.Filter),
        Telescope = Clean(k.Telescope),
        ReadoutMode = Clean(k.ReadoutMode),
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Lower(string? s) => s?.ToLowerInvariant();

    private static bool SameText(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameNumber(double? a, double? b) =>
        (a is null && b is null) || (a is { } x && b is { } y && Math.Abs(x - y) < 1e-6);

    private static double ArcDegrees(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    private static string Num(double? v) => v is { } d ? d.ToString("0.##", CultureInfo.InvariantCulture) : "none";

    /// <summary>The first key, in the order camera, filter, binning, gain, offset, telescope,
    /// rotation, readout, on which the flat disagrees with the light, as "key value"; null if none.</summary>
    private static string? FirstMismatch(FrameKeys light, FrameKeys flat)
    {
        var l = Normalise(light);
        var f = Normalise(flat);
        if (!SameText(l.Camera, f.Camera)) return $"camera {f.Camera ?? "none"}";
        if (!SameText(l.Filter, f.Filter)) return $"filter {f.Filter ?? "none"}";
        if (!SameNumber(l.Binning, f.Binning)) return $"binning {Num(f.Binning)}";
        if (!SameNumber(l.Gain, f.Gain)) return $"gain {Num(f.Gain)}";
        if (!SameNumber(l.Offset, f.Offset)) return $"offset {Num(f.Offset)}";
        if (l.Telescope is not null && f.Telescope is not null && !SameText(l.Telescope, f.Telescope)) return $"telescope {f.Telescope}";
        if (l.RotatorAngle is { } lr && f.RotatorAngle is { } fr && ArcDegrees(lr, fr) > RotatorToleranceDegrees) return $"rotation {Num(fr)}";
        if (l.ReadoutMode is not null && f.ReadoutMode is not null && !SameText(l.ReadoutMode, f.ReadoutMode)) return $"readout {f.ReadoutMode}";
        return null;
    }

    private static bool DarkFlatFollows(ScannedFrame flat, ScannedFrame dark)
    {
        var a = Normalise(flat.Keys);
        var b = Normalise(dark.Keys);
        return SameText(a.Camera, b.Camera) && SameNumber(a.Gain, b.Gain) && SameNumber(a.Offset, b.Offset) && SameNumber(a.Binning, b.Binning)
            && flat.ExposureSeconds is { } fe && dark.ExposureSeconds is { } de && Math.Abs(fe - de) <= FlatDarkExposureToleranceSeconds;
    }

    /// <summary>Candidates on the reference night, else those at the smallest day distance (ties in
    /// both directions kept). Candidates with an unknown night rank last.</summary>
    private static List<ScannedFrame> Nearest(IEnumerable<ScannedFrame> candidates, DateOnly night)
    {
        var ranked = candidates
            .Select(c => (Frame: c, Days: c.Night is { } n ? Math.Abs(n.DayNumber - night.DayNumber) : int.MaxValue))
            .ToList();
        if (ranked.Count == 0) return [];
        int best = ranked.Min(x => x.Days);
        return ranked.Where(x => x.Days == best).Select(x => x.Frame).ToList();
    }

    private static string RejectNote(ScannedFrame f, IReadOnlyList<LightSet> lightSets) => f.Type switch
    {
        CalibrationType.Dark => "darks, not selected",
        CalibrationType.Bias => "bias, not selected",
        CalibrationType.DarkFlat => "flat-darks, no matching flat",
        _ when lightSets.Count == 0 => "no light sets",
        _ => FirstMismatch(SameFilterOrFirst(lightSets, f.Keys).Keys, f.Keys) ?? "not the nearest night",
    };

    // The light set a rejected flat is explained against: the first with its filter, so a
    // wrong-angle Oiii flat reads "rotation", not "filter Oiii" against an Ha set.
    private static LightSet SameFilterOrFirst(IReadOnlyList<LightSet> lightSets, FrameKeys flat)
        => lightSets.FirstOrDefault(set => SameText(Clean(set.Keys.Filter), Clean(flat.Filter))) ?? lightSets[0];
}
