using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Entities;

namespace GalactiLog.Data.Queries;

/// <summary>One (target, night, frame label, raw filter) group of LIGHT frames, the unit every
/// mosaic figure is summed from. <see cref="Label"/> is the stored <c>images.panel_label</c>.</summary>
internal sealed record FrameBucket(Guid TargetId, DateOnly Date, string? Label, string? Filter, int Frames, double Seconds);

/// <summary>One LIGHT frame with what spec 11.4's best frame needs: its triple, raw filter,
/// exposure, capture date and the five scored metrics.</summary>
internal sealed record LightFrame(
    Guid Id, string FilePath, Guid TargetId, DateOnly Date, string? Label, string? Filter, double Seconds,
    DateTime? CaptureDate, FrameMetrics Metrics);

/// <summary>A (target, night, frame label) triple with the label folded the way the unique index
/// of spec 5.24 folds it: null and empty are one value, case is ignored.</summary>
internal readonly record struct TripleKey(Guid TargetId, DateOnly Date, string Label)
{
    public static TripleKey Of(Guid targetId, DateOnly date, string? label) => new(targetId, date, Fold(label));

    public static string Fold(string? label) => (label ?? "").ToUpperInvariant();
}

/// <summary>
/// The membership join of spec 5.24 as the mosaic reads and writes share it (design lesson 1):
/// the frames a panel counts are the LIGHT frames whose target, night and label match one of its
/// <c>included</c> rows, a null label matching a null frame label. The SQL groups frames by triple
/// and raw filter; everything above that is in memory, over at most the contributing targets'
/// frames.
/// </summary>
internal static class MosaicFrames
{
    /// <summary>Every LIGHT frame group of the given targets, nights with no <c>session_date</c>
    /// excluded (spec 5.24: such a frame belongs to no panel).</summary>
    public static List<FrameBucket> Buckets(GalactiLogContext context, IEnumerable<Guid> targetIds)
    {
        var ids = targetIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return [.. LightImages(context, ids)
            .GroupBy(image => new { image.ResolvedTargetId, image.SessionDate, image.PanelLabel, image.FilterUsed })
            .Select(group => new
            {
                group.Key.ResolvedTargetId,
                group.Key.SessionDate,
                group.Key.PanelLabel,
                group.Key.FilterUsed,
                Frames = group.Count(),
                Seconds = group.Sum(image => image.ExposureTime ?? 0d),
            })
            .AsEnumerable()
            .Select(row => new FrameBucket(
                row.ResolvedTargetId!.Value, row.SessionDate!.Value, row.PanelLabel, row.FilterUsed, row.Frames, row.Seconds))];
    }

    /// <summary>Every LIGHT frame of the given targets one by one, the frame-level reach of the
    /// join that spec 11.4's best frame scores; nights with no <c>session_date</c> excluded, as in
    /// <see cref="Buckets"/>.</summary>
    public static List<LightFrame> Frames(GalactiLogContext context, IEnumerable<Guid> targetIds)
    {
        var ids = targetIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return [.. LightImages(context, ids)
            .Select(image => new
            {
                image.Id,
                image.FilePath,
                image.ResolvedTargetId,
                image.SessionDate,
                image.PanelLabel,
                image.FilterUsed,
                image.ExposureTime,
                image.CaptureDate,
                image.DetectedStars,
                image.MedianHfr,
                image.Eccentricity,
                image.GuidingRmsArcsec,
                image.Fwhm,
            })
            .AsEnumerable()
            .Select(row => new LightFrame(
                row.Id, row.FilePath, row.ResolvedTargetId!.Value, row.SessionDate!.Value, row.PanelLabel, row.FilterUsed,
                row.ExposureTime ?? 0d, row.CaptureDate,
                new FrameMetrics(row.DetectedStars, row.MedianHfr, row.Eccentricity, row.GuidingRmsArcsec, row.Fwhm)))];
    }

    private static IQueryable<Image> LightImages(GalactiLogContext context, List<Guid> ids)
        => context.Images.Where(image => image.ImageType == "LIGHT"
            && image.ResolvedTargetId != null
            && image.SessionDate != null
            && ids.Contains(image.ResolvedTargetId.Value));

    /// <summary>The distinct triples the buckets carry, each in the spelling of its first bucket,
    /// ordered by night, target and label so every caller writes and lists them in one order.</summary>
    public static List<(Guid TargetId, DateOnly Date, string? Label)> Triples(IEnumerable<FrameBucket> buckets)
        => [.. buckets
            .GroupBy(bucket => TripleKey.Of(bucket.TargetId, bucket.Date, bucket.Label))
            .Select(group => (group.Key.TargetId, group.Key.Date, group.First().Label))
            .OrderBy(triple => triple.Date)
            .ThenBy(triple => triple.TargetId)
            .ThenBy(triple => TripleKey.Fold(triple.Label), StringComparer.Ordinal)];

    /// <summary>Every triple an <c>included</c> row of the mosaic holds, with the panel holding
    /// it. The one-triple rule of spec 5.24 makes the key unique; a hand-edited duplicate keeps the
    /// first panel in <c>sort_order</c>.</summary>
    public static Dictionary<TripleKey, MosaicPanel> IncludedTriples(GalactiLogContext context, Guid mosaicId)
    {
        var rows = (from session in context.MosaicPanelSessions
                    join panel in context.MosaicPanels on session.PanelId equals panel.Id
                    where panel.MosaicId == mosaicId && session.Status == MosaicPanelSession.Included
                    orderby panel.SortOrder
                    select new { session.TargetId, session.SessionDate, session.FrameLabel, Panel = panel })
                   .ToList();

        var map = new Dictionary<TripleKey, MosaicPanel>();
        foreach (var row in rows)
        {
            map.TryAdd(TripleKey.Of(row.TargetId, row.SessionDate, row.FrameLabel), row.Panel);
        }

        return map;
    }

    /// <summary>Spec 12.17's Available triples of one panel: the distinct triples of the LIGHT
    /// frames of every target with any row on the panel, except those included anywhere in the
    /// mosaic and those whose frame label is another panel's label of the mosaic (case
    /// insensitive). A null or unmatched label stays offered to every panel.</summary>
    public static List<(Guid TargetId, DateOnly Date, string? Label)> AvailableTriples(
        IEnumerable<FrameBucket> contributorBuckets, IReadOnlySet<Guid> contributors, IReadOnlyDictionary<TripleKey, MosaicPanel> included,
        IEnumerable<string> otherPanelLabels)
    {
        var others = otherPanelLabels.Select(TripleKey.Fold).ToHashSet(StringComparer.Ordinal);
        return [.. Triples(contributorBuckets.Where(bucket => contributors.Contains(bucket.TargetId)))
            .Where(triple => !included.ContainsKey(TripleKey.Of(triple.TargetId, triple.Date, triple.Label))
                && (triple.Label is null || !others.Contains(TripleKey.Fold(triple.Label))))];
    }

    /// <summary>A stored frame label: trimmed, and null for no label (spec 5.24: never the empty
    /// string).</summary>
    public static string? NormalizeLabel(string? label)
        => string.IsNullOrWhiteSpace(label) ? null : label.Trim();
}
