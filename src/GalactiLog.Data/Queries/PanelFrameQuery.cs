using GalactiLog.Core.Aliases;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Repositories;

namespace GalactiLog.Data.Queries;

/// <summary>The frame a tile shows (spec 11.4): the image id, its path (for
/// <c>ThumbnailWorker.RequestFrame</c>), its canonical filter and its score.</summary>
public sealed record BestFrame(Guid ImageId, string FilePath, string Filter, double Score);

/// <summary>Spec 12.17's filter selector inputs for one mosaic: the available filters in order,
/// the default filter (null when there is none), and the best frame per panel per filter. The
/// outer key is the panel id, every panel of the mosaic present; the inner dictionary is keyed by
/// canonical filter, ordinal case insensitive, and holds an entry only for a filter the panel has
/// a frame in.</summary>
public sealed record PanelFrameSet(
    IReadOnlyList<string> AvailableFilters,
    string? DefaultFilter,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, BestFrame>> BestByPanel);

/// <summary>
/// Spec 11.4's best frame per panel and default filter, the port of the web's
/// <c>score_frames</c> and <c>find_default_filter</c> (<c>mosaic_composite.py</c>), over the LIGHT
/// frames the membership join of spec 5.24 reaches (<see cref="MosaicFrames"/>). A frame with no
/// canonical filter takes no part.
/// </summary>
public sealed class PanelFrameQuery(DatabaseConnectionString connectionString, AliasMapCache aliases)
{
    private sealed record Candidate(LightFrame Frame, string Filter);

    /// <summary>One read for the whole mosaic: every LIGHT frame the membership join reaches,
    /// pooled per panel per canonical filter, scored, the top of each pool kept. Also the
    /// available filters (summed <c>exposure_time</c> descending, ties by name ordinal case
    /// insensitive) and the default filter of spec 11.4.</summary>
    public PanelFrameSet ForMosaic(Guid mosaicId)
    {
        using var context = Open();
        var map = aliases.Current;
        var panels = context.MosaicPanels.Where(panel => panel.MosaicId == mosaicId)
            .OrderBy(panel => panel.SortOrder).Select(panel => panel.Id).ToList();
        var included = MosaicFrames.IncludedTriples(context, mosaicId);
        var byPanel = MosaicFrames.Frames(context, included.Keys.Select(key => key.TargetId))
            .Select(frame => (Frame: frame, Filter: map.CanonicalFilter(frame.Filter),
                Panel: included.GetValueOrDefault(TripleKey.Of(frame.TargetId, frame.Date, frame.Label))))
            .Where(row => row.Panel is not null && row.Filter is { Length: > 0 })
            .ToLookup(row => row.Panel!.Id, row => new Candidate(row.Frame, row.Filter!));

        var available = byPanel.SelectMany(pool => pool)
            .GroupBy(candidate => candidate.Filter, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Filter: group.Key, Seconds: group.Sum(candidate => candidate.Frame.Seconds)))
            .OrderByDescending(filter => filter.Seconds)
            .ThenBy(filter => filter.Filter, StringComparer.OrdinalIgnoreCase)
            .Select(filter => filter.Filter)
            .ToList();

        // find_default_filter: each panel's top frame over all its filters, the first panel in
        // sort_order winning a tie.
        BestFrame? leader = null;
        foreach (var panel in panels)
        {
            if (Best(byPanel[panel]) is { } top && (leader is null || top.Score > leader.Score))
            {
                leader = top;
            }
        }

        var best = panels.ToDictionary(
            panel => panel,
            panel => (IReadOnlyDictionary<string, BestFrame>)byPanel[panel]
                .GroupBy(candidate => candidate.Filter, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Best(group)!, StringComparer.OrdinalIgnoreCase));

        return new PanelFrameSet(available, leader?.Filter ?? available.FirstOrDefault(), best);
    }

    /// <summary>The read-only preview's tile (spec 12.17): the best frame, over every filter,
    /// among the target's LIGHT frames on the given nights whose stored <c>panel_label</c> equals
    /// the label (case insensitive, the session table's rule). Null when there is none.</summary>
    public BestFrame? ForSuggestionEntry(Guid targetId, string label, IReadOnlyCollection<DateOnly> nights)
    {
        if (nights.Count == 0)
        {
            return null;
        }

        using var context = Open();
        var map = aliases.Current;
        var wanted = nights.ToHashSet();
        var folded = TripleKey.Fold(label);
        return Best(MosaicFrames.Frames(context, [targetId])
            .Where(frame => frame.Label is not null && wanted.Contains(frame.Date) && TripleKey.Fold(frame.Label) == folded)
            .Select(frame => (Frame: frame, Filter: map.CanonicalFilter(frame.Filter)))
            .Where(row => row.Filter is { Length: > 0 })
            .Select(row => new Candidate(row.Frame, row.Filter!)));
    }

    /// <summary>Spec 11.6's geometry read (ruling R10): the six <c>images</c> columns
    /// <c>ra_deg</c>, <c>dec_deg</c>, <c>width_px</c>, <c>arcsec_per_pixel</c>,
    /// <c>rotator_position</c> and <c>pier_side</c> of the given images, one read, never from
    /// <c>raw_headers</c>. An id with no row is absent from the result. The web reads these from
    /// the FITS header in <c>build_mosaic_composite</c>.</summary>
    public IReadOnlyDictionary<Guid, PanelGeometry> Geometry(IReadOnlyCollection<Guid> imageIds)
    {
        if (imageIds.Count == 0)
        {
            return new Dictionary<Guid, PanelGeometry>();
        }

        using var context = Open();
        return context.Images.Where(image => imageIds.Contains(image.Id))
            .Select(image => new { image.Id, image.RaDeg, image.DecDeg, image.WidthPx, image.ArcsecPerPixel, image.RotatorPosition, image.PierSide })
            .AsEnumerable()
            .ToDictionary(
                row => row.Id,
                row => new PanelGeometry(row.RaDeg, row.DecDeg, row.WidthPx, row.ArcsecPerPixel, row.RotatorPosition, row.PierSide));
    }

    /// <summary>The top of one pool by <see cref="FrameScore.Score"/>, ties to the most recent
    /// <c>capture_date</c> (null last), then the ordinally first <c>file_path</c>; null for an
    /// empty pool.</summary>
    private static BestFrame? Best(IEnumerable<Candidate> pool)
    {
        var candidates = pool.ToList();
        var scores = FrameScore.Score([.. candidates.Select(candidate => candidate.Frame.Metrics)]);
        return candidates.Zip(scores)
            .OrderByDescending(pair => pair.Second)
            .ThenByDescending(pair => pair.First.Frame.CaptureDate)
            .ThenBy(pair => pair.First.Frame.FilePath, StringComparer.Ordinal)
            .Select(pair => new BestFrame(pair.First.Frame.Id, pair.First.Frame.FilePath, pair.First.Filter, pair.Second))
            .FirstOrDefault();
    }

    private GalactiLogContext Open() => new(GalactiLogContextOptions.Create(connectionString.Value));
}
