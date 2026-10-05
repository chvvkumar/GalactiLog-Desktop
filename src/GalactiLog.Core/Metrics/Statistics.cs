namespace GalactiLog.Core.Metrics;

/// <summary>
/// Port of the statistics helpers the web application takes from Python's <c>statistics</c>
/// module plus <c>services/frame_quality.py</c>. Pure functions, no allocation beyond one sorted
/// copy, no dependency on Core's other namespaces.
/// </summary>
public static class Statistics
{
    /// <summary>Median of the non-null values, averaging the two middle values on an even count
    /// so it matches Python's <c>statistics.median</c> exactly. Null for an empty input.</summary>
    public static double? Median(IEnumerable<double?> values)
    {
        // The local list is the sorted copy: the caller's sequence is never
        // reordered, so passing a live List is safe.
        var present = new List<double>();
        foreach (var value in values)
        {
            if (value is { } number)
            {
                present.Add(number);
            }
        }

        return MedianOfCollected(present);
    }

    /// <summary>Median of <paramref name="values"/>, averaging the two middle values on an even
    /// count so it matches Python's <c>statistics.median</c> exactly. Null for an empty input.
    /// The non-nullable twin of <see cref="Median(IEnumerable{double?})"/>, for the callers that
    /// never carry a null and would otherwise box every value to call it (ruling A5: one
    /// median).</summary>
    public static double? Median(IEnumerable<double> values) => MedianOfCollected([.. values]);

    // The one sort, the one middle index and the one even-count average behind both overloads.
    private static double? MedianOfCollected(List<double> present)
    {
        if (present.Count == 0)
        {
            return null;
        }

        present.Sort();
        var middle = present.Count / 2;
        return present.Count % 2 == 1
            ? present[middle]
            : (present[middle - 1] + present[middle]) / 2d;
    }

    /// <summary>Arithmetic mean of the non-null values. Null for an empty input. This is what
    /// spec 12.4's totals row means by "average"; the session cards use <see cref="Median"/>.
    /// The two are deliberately different and the web application is the reason: totals are
    /// means, per-session figures are medians.</summary>
    public static double? Mean(IEnumerable<double?> values)
    {
        var sum = 0d;
        var count = 0;
        foreach (var value in values)
        {
            if (value is { } number)
            {
                sum += number;
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }

    /// <summary>Median absolute deviation from the median of the non-null values: the robust
    /// dispersion the frame-quality baselines grade against. Null for an empty input and
    /// <c>0</c> for a uniform group. Port of <c>frame_quality.mad</c>.</summary>
    /// <remarks>Raw MAD, with no 1.4826 consistency scaling anywhere: the web application omits
    /// it deliberately, so a z-score built on this is in MAD units and is not a sigma. Adding the
    /// scaling here would silently move every outlier threshold in
    /// <see cref="FrameQuality"/>.</remarks>
    public static double? Mad(IEnumerable<double?> values)
    {
        var present = new List<double>();
        foreach (var value in values)
        {
            if (value is { } number)
            {
                present.Add(number);
            }
        }

        if (present.Count == 0)
        {
            return null;
        }

        // Median over the same code path as every other median in the port, so an even count is
        // averaged identically on both passes.
        var median = Median(present.Select(number => (double?)number))!.Value;
        return Median(present.Select(number => (double?)Math.Abs(number - median)));
    }
}
