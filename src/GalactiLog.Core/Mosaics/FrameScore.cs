namespace GalactiLog.Core.Mosaics;

/// <summary>The five metrics spec 11.4 scores. Null means the frame has no value.</summary>
public sealed record FrameMetrics(int? DetectedStars, double? MedianHfr, double? Eccentricity, double? GuidingRmsArcsec, double? Fwhm);

/// <summary>
/// Spec 11.4's best-frame score, the port of the web's <c>score_frames</c>
/// (<c>mosaic_composite.py</c>): each metric min-max normalised within the pool over the values
/// present and above zero, then weighted and summed. The caller picks the top frame and breaks
/// ties (<c>GalactiLog.Data.Queries.PanelFrameQuery</c>).
/// </summary>
public static class FrameScore
{
    private static readonly (Func<FrameMetrics, double?> Value, double Weight, bool HigherIsBetter)[] Metrics =
    [
        (frame => frame.DetectedStars, 0.35, true),
        (frame => frame.MedianHfr, 0.30, false),
        (frame => frame.Eccentricity, 0.15, false),
        (frame => frame.GuidingRmsArcsec, 0.12, false),
        (frame => frame.Fwhm, 0.08, false),
    ];

    /// <summary>One score per frame, same order as the pool, spec 11.4's rule: from 0 to 1, a
    /// missing or non-positive value scoring 0.5 on its metric, a metric with fewer than two such
    /// values scoring 0.5 for every frame, a pool of one scoring 1.0.</summary>
    public static IReadOnlyList<double> Score(IReadOnlyList<FrameMetrics> pool)
    {
        if (pool.Count <= 1)
        {
            return pool.Count == 0 ? [] : [1.0];
        }

        var scores = new double[pool.Count];
        foreach (var (value, weight, higherIsBetter) in Metrics)
        {
            var present = pool.Select(value).Where(v => v > 0).Select(v => v!.Value).ToList();
            var min = present.Count < 2 ? 0 : present.Min();
            var max = present.Count < 2 ? 0 : present.Max();
            var span = max != min ? max - min : 1.0;
            for (var i = 0; i < pool.Count; i++)
            {
                var normalised = 0.5;
                if (present.Count >= 2 && value(pool[i]) is double v and > 0)
                {
                    var n = (v - min) / span;
                    normalised = higherIsBetter ? n : 1.0 - n;
                }

                scores[i] += normalised * weight;
            }
        }

        return scores;
    }
}
