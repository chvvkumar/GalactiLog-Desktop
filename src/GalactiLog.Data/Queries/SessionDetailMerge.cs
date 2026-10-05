using System.Globalization;
using GalactiLog.Core.Metrics;

namespace GalactiLog.Data.Queries;

/// <summary>
/// Phase 25 R3: folds several nights' <see cref="SessionDetail"/> records into one merged
/// pseudo-night, so the Night review pane shows a multi-night selection through the one card it
/// already has. Pure functions over loaded details and overviews; no database.
/// </summary>
/// <remarks>
/// Every figure that the query derives from frames is rebuilt here over the union of the members'
/// frames through the query's own builders, so a merged card's per-filter rows, ranges and rig
/// labels follow exactly the rules a single night's do. The per-night work the merge does not
/// repeat is the grading and the outlier flags: those stay as each night computed them, against
/// its own baselines (spec 12.4).
/// </remarks>
public static class SessionDetailMerge
{
    /// <summary>The merged detail over <paramref name="nights"/>, which arrive oldest first. A
    /// list of one returns that detail itself; an empty list is a caller error.</summary>
    public static SessionDetail Merge(IReadOnlyList<SessionDetail> nights)
    {
        if (nights.Count == 0)
        {
            throw new ArgumentException("At least one night is required.", nameof(nights));
        }

        if (nights.Count == 1)
        {
            return nights[0];
        }

        List<FrameRow> frames = [.. nights.SelectMany(night => night.Frames)];

        List<NightSpan> spans = [];
        var offset = 0;
        foreach (var night in nights)
        {
            spans.Add(new NightSpan(night.SessionDate, offset, night.Frames.Count));
            offset += night.Frames.Count;
        }

        // Spec 7.2 over the union: one modal source decides every eccentricity figure on the
        // merged card, which can differ from a member's own source.
        EccentricitySources.TryGetModalSource(
            frames.Select(frame => (frame.EccentricitySource, frame.Eccentricity)),
            out var modalSource);
        var excludedEccentricity = frames.Count(frame =>
            frame.Eccentricity is not null
            && !EccentricitySources.IsPooled(frame.EccentricitySource, frame.Eccentricity, modalSource));

        var arcsec = SessionDetailQuery.HfrArcsec(frames);
        var rigOrder = SessionDetailQuery.RigOrder(frames);
        var reference = SessionDetailQuery.RankReference(frames);

        return new SessionDetail(
            nights[0].GroupKey,
            nights[0].SessionDate,
            nights.Sum(night => night.FrameCount),
            nights.Sum(night => night.IntegrationSeconds),
            SessionDetailQuery.Range(frames.Select(frame => frame.MedianHfr)),
            Statistics.Median(arcsec.Values),
            arcsec.ExcludedCount,
            SessionDetailQuery.Range(frames.Select(frame => SessionDetailQuery.PooledEccentricity(frame, modalSource))),
            modalSource,
            excludedEccentricity,
            SessionDetailQuery.Range(frames.Select(frame => frame.Fwhm)),
            SessionDetailQuery.Range(frames.Select(frame => frame.GuidingRmsArcsec)),
            SessionDetailQuery.Range(frames.Select(frame => frame.SensorTemp)),
            // A merged card has no one first frame to borrow a gain from, so it takes the mode.
            SessionDetailQuery.ModalGain(frames.Select(frame => frame.CameraGain)),
            [.. frames.Select(frame => frame.ExposureTime).OfType<double>().Distinct().Order()],
            nights.Min(night => night.FirstFrameTime),
            nights.Max(night => night.LastFrameTime),
            SessionDetailQuery.SplitPerRig(frames, rigOrder, group => SessionDetailQuery.BuildFilterMedians(group, modalSource)),
            SessionDetailQuery.SplitPerRig(frames, rigOrder, group => SessionDetailQuery.BuildFilterDetails(group, modalSource)),
            Statistics.Median(frames.Select(frame => frame.Airmass)),
            Statistics.Median(frames.Select(frame => frame.AmbientTemp)),
            Statistics.Median(frames.Select(frame => frame.Humidity)),
            [.. nights.SelectMany(night => night.Insights.Select(insight =>
                insight with { Message = InsightPrefix(night.SessionDate) + insight.Message }))],
            frames,
            null,
            MergeRigs(nights, frames, rigOrder, modalSource),
            reference.ImageId,
            reference.Paths.Count > 0 ? reference.Paths[0] : null,
            reference.Paths,
            SessionDetailQuery.SplitPerRig(frames, rigOrder, SessionDetailQuery.BuildFilterAcquisitions))
        {
            Nights = spans,
        };
    }

    /// <summary>The merged card's overview from the members' overviews alone, in any order,
    /// because the card factory needs it before the merged detail has been read (R3, last
    /// bullet). The medians are medians of medians, read only by the comparison ink.</summary>
    public static SessionOverview Overview(IReadOnlyList<SessionOverview> nights)
    {
        if (nights.Count == 0)
        {
            throw new ArgumentException("At least one night is required.", nameof(nights));
        }

        List<SessionOverview> ordered = [.. nights.OrderBy(night => night.SessionDate)];

        List<string> filters = [];
        foreach (var filter in ordered.SelectMany(night => night.FiltersUsed))
        {
            if (!filters.Contains(filter, StringComparer.Ordinal))
            {
                filters.Add(filter);
            }
        }

        var pairs = nights.Select(night => (night.Telescope, night.Camera)).Distinct().Count();

        List<GuidingRmsProvenance> guided = [.. nights
            .Select(night => night.GuidingProvenance)
            .Where(provenance => provenance != GuidingRmsProvenance.None)
            .Distinct()];

        return new SessionOverview(
            ordered[0].SessionDate,
            nights.Sum(night => night.IntegrationSeconds),
            nights.Sum(night => night.FrameCount),
            Statistics.Median(nights.Select(night => night.MedianHfr)),
            Statistics.Median(nights.Select(night => night.MedianHfrArcsec)),
            nights.Sum(night => night.HfrArcsecExcludedCount),
            Statistics.Median(nights.Select(night => night.MedianEccentricity)),
            Agreed(nights.Select(night => night.EccentricitySource)),
            Statistics.Median(nights.Select(night => night.MedianFwhm)),
            Statistics.Median(nights.Select(night => night.MedianGuidingRmsArcsec)),
            Statistics.Median(nights.Select(night => night.MedianDetectedStars)),
            filters,
            Agreed(nights.Select(night => night.Camera)),
            Agreed(nights.Select(night => night.Telescope)),
            Math.Max(pairs, nights.Max(night => night.RigCount)),
            nights.Any(night => night.HasNotes),
            guided switch
            {
                [] => GuidingRmsProvenance.None,
                [var one] => one,
                _ => GuidingRmsProvenance.Mixed,
            },
            nights.Sum(night => night.GuidingSessionCount));
    }

    /// <summary>The prefix a merged card's insight carries, naming the night it came from.</summary>
    public static string InsightPrefix(DateOnly night)
        => night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": ";

    /// <summary>The one value when every member reports it, else null.</summary>
    private static string? Agreed(IEnumerable<string?> values)
        => values.Distinct(StringComparer.Ordinal).ToList() is [var one] ? one : null;

    /// <summary>R3 rigs: one group per label in first-appearance order over the union, the
    /// figures over the rig's union frames on the query's own rules, the equipment halves from the
    /// first member that reported the label.</summary>
    private static IReadOnlyList<RigGroup> MergeRigs(
        IReadOnlyList<SessionDetail> nights,
        IReadOnlyList<FrameRow> frames,
        IReadOnlyList<string> rigOrder,
        string? modalSource)
    {
        var multiRig = rigOrder.Count > 1;
        List<RigGroup> groups = [];

        for (var index = 0; index < rigOrder.Count; index++)
        {
            var label = rigOrder[index];
            List<FrameRow> own = [.. frames.Where(frame => SessionDetailQuery.IsRig(frame, label))];
            var first = nights
                .SelectMany(night => night.Rigs ?? [])
                .FirstOrDefault(rig => string.Equals(rig.Label, label, StringComparison.Ordinal));
            var reference = SessionDetailQuery.RankReference(own);

            groups.Add(new RigGroup(
                index,
                label,
                first?.Telescope,
                first?.Camera,
                own.Count,
                own.Sum(frame => frame.ExposureTime ?? 0d),
                reference.ImageId,
                reference.Paths.Count > 0 ? reference.Paths[0] : null,
                reference.Paths,
                multiRig
                    ?
                    [
                        SessionDetailQuery.Range(own.Select(frame => frame.MedianHfr)),
                        SessionDetailQuery.Range(own.Select(frame => SessionDetailQuery.PooledEccentricity(frame, modalSource))),
                        SessionDetailQuery.Range(own.Select(frame => frame.Fwhm)),
                        SessionDetailQuery.Range(own.Select(frame => frame.GuidingRmsArcsec)),
                        SessionDetailQuery.Range(own.Select(frame => frame.SensorTemp)),
                    ]
                    : null));
        }

        return groups;
    }
}
