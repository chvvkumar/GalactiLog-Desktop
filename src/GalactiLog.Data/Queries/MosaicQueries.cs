using GalactiLog.Core.Aliases;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One row of the Mosaics table (spec 12.17). The date range is the first and last night
/// the mosaic counts a frame on, null when it counts none; filters are canonical names (spec
/// 5.8.4) in ordinal case-insensitive order.</summary>
public sealed record MosaicListRow(
    Guid Id,
    string Name,
    int Panels,
    double IntegrationSeconds,
    int Frames,
    DateOnly? FirstNight,
    DateOnly? LastNight,
    IReadOnlyList<string> Filters);

/// <summary>One (target, night, frame label) triple of a panel, included or available, with the
/// figures of the LIGHT frames that triple admits (spec 12.17). <see cref="FrameLabel"/> null is
/// the "No label" row (ruling R19a).</summary>
public sealed record PanelNight(
    Guid TargetId,
    string TargetName,
    DateOnly Date,
    string? FrameLabel,
    IReadOnlyDictionary<string, int> FramesByFilter,
    int Frames,
    double IntegrationSeconds);

/// <summary>One panel of the mosaic detail page (spec 12.17). Its figures count the frames of its
/// <see cref="Included"/> triples; <see cref="TargetIds"/> are the targets of those rows;
/// <see cref="NightsIncluded"/> is the distinct (target, night) pairs among them;
/// <see cref="NightsAvailable"/> is the length of <see cref="Available"/>; the deficit is the
/// leading panel's integration minus this one's, zero on the leader and while every panel is zero.
/// <see cref="IntegrationByFilter"/> is seconds per canonical filter, for the CSV.</summary>
public sealed record PanelDetail(
    Guid Id,
    string Label,
    int SortOrder,
    double? CanvasX,
    double? CanvasY,
    int Rotation,
    bool FlipH,
    IReadOnlyList<Guid> TargetIds,
    IReadOnlyList<string> TargetNames,
    double IntegrationSeconds,
    int Frames,
    int NightsIncluded,
    int NightsAvailable,
    double DeficitSeconds,
    IReadOnlyList<PanelNight> Included,
    IReadOnlyList<PanelNight> Available,
    IReadOnlyDictionary<string, double> IntegrationByFilter);

/// <summary>One chip of the available labels banner (spec 12.17): a label carried by LIGHT frames
/// of a contributing target that no panel of the mosaic carries.</summary>
public sealed record AvailableLabel(Guid TargetId, string TargetName, string Label);

/// <summary>The mosaic detail page's whole read (spec 12.17): the header, the summary figures
/// (sums over the panels), the panels in <c>sort_order</c> and the available labels.</summary>
public sealed record MosaicDetail(
    Guid Id,
    string Name,
    string? Notes,
    double RotationAngle,
    double IntegrationSeconds,
    int Frames,
    DateOnly? FirstNight,
    DateOnly? LastNight,
    IReadOnlyList<string> Filters,
    IReadOnlyList<PanelDetail> Panels,
    IReadOnlyList<AvailableLabel> AvailableLabels);

/// <summary>One line of Export panels (CSV), spec 12.17, projected from a <see cref="MosaicDetail"/>
/// so the export has no query of its own. <see cref="IntegrationByFilter"/> is in ordinal
/// case-insensitive filter order.</summary>
public sealed record PanelCsvRow(
    string Label,
    IReadOnlyList<string> Targets,
    int Nights,
    int Frames,
    double IntegrationSeconds,
    IReadOnlyList<KeyValuePair<string, double>> IntegrationByFilter);

/// <summary>A mosaic named by a link or a picker: the dashboard link (spec 12.2) and the Create
/// mosaic dialog's existing list (spec 12.17).</summary>
public sealed record MosaicLink(Guid MosaicId, string Name);

/// <summary>One row of a suggestion's session table (spec 12.17): an entry's frames grouped by
/// <c>OBJECT</c>, night and canonical filter. <see cref="InCampaign"/> is false for a night
/// outside the suggestion's own nights, which the "+k more nights" total counts.</summary>
public sealed record SuggestionSessionRow(
    Guid TargetId,
    string Label,
    string ObjectName,
    DateOnly Night,
    string? Filter,
    int Frames,
    double IntegrationSeconds,
    bool InCampaign);

/// <summary>
/// Spec 12.17's read side for the Mosaics page, the mosaic detail page, the Create mosaic dialog
/// and the dashboard link. Every figure counts the LIGHT frames the membership join of spec 5.24
/// reaches (<see cref="MosaicFrames"/>), and no other.
/// </summary>
public sealed class MosaicQueries(DatabaseConnectionString connectionString, AliasMapCache aliases)
{
    /// <summary>Every mosaic, ordered by name, ordinal and case insensitive.</summary>
    public IReadOnlyList<MosaicListRow> List()
    {
        using var context = Open();
        var map = aliases.Current;
        var mosaics = context.Mosaics.AsEnumerable().OrderBy(mosaic => mosaic.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var panelCounts = context.MosaicPanels.GroupBy(panel => panel.MosaicId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(row => row.Key, row => row.Count);
        var included = (from session in context.MosaicPanelSessions
                        join panel in context.MosaicPanels on session.PanelId equals panel.Id
                        where session.Status == MosaicPanelSession.Included
                        select new { panel.MosaicId, session.TargetId, session.SessionDate, session.FrameLabel })
                       .ToList();
        var buckets = ByTriple(MosaicFrames.Buckets(context, included.Select(row => row.TargetId)));

        return [.. mosaics.Select(mosaic =>
        {
            var counted = included.Where(row => row.MosaicId == mosaic.Id)
                .Select(row => TripleKey.Of(row.TargetId, row.SessionDate, row.FrameLabel))
                .Distinct()
                .SelectMany(key => buckets[key])
                .ToList();
            var (first, last) = Range(counted);
            return new MosaicListRow(
                mosaic.Id, mosaic.Name, panelCounts.GetValueOrDefault(mosaic.Id),
                counted.Sum(bucket => bucket.Seconds), counted.Sum(bucket => bucket.Frames),
                first, last, Filters(counted, map));
        })];
    }

    /// <summary>The detail page's read, or null when the mosaic does not exist.</summary>
    public MosaicDetail? Detail(Guid mosaicId)
    {
        using var context = Open();
        var mosaic = context.Mosaics.SingleOrDefault(row => row.Id == mosaicId);
        if (mosaic is null)
        {
            return null;
        }

        var map = aliases.Current;
        var panels = context.MosaicPanels.Where(panel => panel.MosaicId == mosaicId).OrderBy(panel => panel.SortOrder).ToList();
        var panelIds = panels.Select(panel => panel.Id).ToList();
        var rows = context.MosaicPanelSessions.Where(row => panelIds.Contains(row.PanelId)).ToList();
        var contributors = rows.Select(row => row.TargetId).ToHashSet();
        var names = context.Targets.Where(target => contributors.Contains(target.Id))
            .ToDictionary(target => target.Id, target => target.PrimaryName);
        var allBuckets = MosaicFrames.Buckets(context, contributors);
        var buckets = ByTriple(allBuckets);
        var includedAnywhere = MosaicFrames.IncludedTriples(context, mosaicId);

        PanelNight Night(Guid target, DateOnly date, string? label)
        {
            var counted = buckets[TripleKey.Of(target, date, label)].ToList();
            return new PanelNight(
                target, names.GetValueOrDefault(target, ""), date, label,
                counted.Where(bucket => map.CanonicalFilter(bucket.Filter) is { Length: > 0 })
                    .GroupBy(bucket => map.CanonicalFilter(bucket.Filter)!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(bucket => bucket.Frames), StringComparer.OrdinalIgnoreCase),
                counted.Sum(bucket => bucket.Frames),
                counted.Sum(bucket => bucket.Seconds));
        }

        IEnumerable<PanelNight> NewestFirst(IEnumerable<PanelNight> nights)
            => nights.OrderByDescending(night => night.Date)
                .ThenBy(night => night.TargetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(night => night.FrameLabel ?? "", StringComparer.OrdinalIgnoreCase);

        var drafts = panels.Select(panel =>
        {
            var own = rows.Where(row => row.PanelId == panel.Id).ToList();
            var included = own.Where(row => row.Status == MosaicPanelSession.Included)
                .Select(row => Night(row.TargetId, row.SessionDate, row.FrameLabel))
                .ToList();
            var available = MosaicFrames.AvailableTriples(allBuckets, own.Select(row => row.TargetId).ToHashSet(), includedAnywhere)
                .Select(triple => Night(triple.TargetId, triple.Date, triple.Label));
            var counted = included.SelectMany(night => buckets[TripleKey.Of(night.TargetId, night.Date, night.FrameLabel)]).ToList();
            var targets = included.Select(night => night.TargetId).Distinct()
                .OrderBy(id => names.GetValueOrDefault(id, ""), StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (Panel: panel, Included: NewestFirst(included).ToList(), Available: NewestFirst(available).ToList(),
                Targets: targets, Counted: counted);
        }).ToList();

        var leading = drafts.Count == 0 ? 0d : drafts.Max(draft => draft.Counted.Sum(bucket => bucket.Seconds));
        var details = drafts.Select(draft =>
        {
            var seconds = draft.Counted.Sum(bucket => bucket.Seconds);
            return new PanelDetail(
                draft.Panel.Id, draft.Panel.PanelLabel, draft.Panel.SortOrder, draft.Panel.CanvasX, draft.Panel.CanvasY,
                draft.Panel.Rotation, draft.Panel.FlipH,
                draft.Targets, [.. draft.Targets.Select(id => names.GetValueOrDefault(id, ""))],
                seconds, draft.Counted.Sum(bucket => bucket.Frames),
                draft.Included.Select(night => (night.TargetId, night.Date)).Distinct().Count(),
                draft.Available.Count,
                leading > 0 ? leading - seconds : 0d,
                draft.Included, draft.Available,
                IntegrationByFilter(draft.Counted, map));
        }).ToList();

        var allCounted = drafts.SelectMany(draft => draft.Counted).ToList();
        var (first, last) = Range(allCounted);
        var available = allBuckets
            .Where(bucket => bucket.Label is not null && !panels.Any(panel => SameLabel(panel.PanelLabel, bucket.Label)))
            .GroupBy(bucket => (bucket.TargetId, Label: TripleKey.Fold(bucket.Label)))
            .Select(group => new AvailableLabel(group.Key.TargetId, names.GetValueOrDefault(group.Key.TargetId, ""), group.First().Label!))
            .OrderBy(label => label.TargetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(label => label.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MosaicDetail(
            mosaic.Id, mosaic.Name, mosaic.Notes, mosaic.RotationAngle,
            details.Sum(panel => panel.IntegrationSeconds), details.Sum(panel => panel.Frames),
            first, last, Filters(allCounted, map), details, available);
    }

    /// <summary>Export panels (CSV), one row per panel in <c>sort_order</c>, from the figures
    /// already on the page (spec 12.17).</summary>
    public static IReadOnlyList<PanelCsvRow> CsvRows(MosaicDetail detail)
        => [.. detail.Panels.Select(panel => new PanelCsvRow(
            panel.Label, panel.TargetNames, panel.NightsIncluded, panel.Frames, panel.IntegrationSeconds,
            [.. panel.IntegrationByFilter
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)]))];

    /// <summary>Spec 12.2's link set: every target with an <c>included</c> row in some panel of
    /// some mosaic, mapped to its mosaics in ordinal case-insensitive name order. An
    /// <c>available</c> row alone carries no link.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<MosaicLink>> MosaicLinksByTarget()
    {
        using var context = Open();
        var rows = (from session in context.MosaicPanelSessions
                    join panel in context.MosaicPanels on session.PanelId equals panel.Id
                    join mosaic in context.Mosaics on panel.MosaicId equals mosaic.Id
                    where session.Status == MosaicPanelSession.Included
                    select new { session.TargetId, mosaic.Id, mosaic.Name })
                   .Distinct()
                   .ToList();

        return rows.GroupBy(row => row.TargetId).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<MosaicLink>)[.. group
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Id)
                .Select(row => new MosaicLink(row.Id, row.Name))]);
    }

    /// <summary>The targets <see cref="MosaicLinksByTarget"/> names.</summary>
    public IReadOnlySet<Guid> TargetsInAnyMosaic() => MosaicLinksByTarget().Keys.ToHashSet();

    /// <summary>The mosaic a dashboard link opens: the first of the target's mosaics by name
    /// (spec 12.2).</summary>
    public IReadOnlyDictionary<Guid, Guid> MosaicByTarget()
        => MosaicLinksByTarget().ToDictionary(pair => pair.Key, pair => pair.Value[0].MosaicId);

    /// <summary>The Create mosaic dialog's existing list: every mosaic with at least one row, of
    /// either status, naming the target, ordered by name.</summary>
    public IReadOnlyList<MosaicLink> MosaicsIncludingTarget(Guid targetId)
    {
        using var context = Open();
        return [.. (from session in context.MosaicPanelSessions
                    join panel in context.MosaicPanels on session.PanelId equals panel.Id
                    join mosaic in context.Mosaics on panel.MosaicId equals mosaic.Id
                    where session.TargetId == targetId
                    select new { mosaic.Id, mosaic.Name })
                   .Distinct()
                   .AsEnumerable()
                   .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(row => row.Id)
                   .Select(row => new MosaicLink(row.Id, row.Name))];
    }

    /// <summary>A suggestion's session table (spec 12.17): for each entry, its target's LIGHT
    /// frames whose stored <c>panel_label</c> is the entry's label, compared case insensitively,
    /// grouped by <c>OBJECT</c>, night and canonical filter, ordered by entry, <c>OBJECT</c>,
    /// night and filter.</summary>
    public IReadOnlyList<SuggestionSessionRow> SuggestionSessions(MosaicSuggestionRow suggestion)
    {
        if (suggestion.Panels.Count == 0)
        {
            return [];
        }

        using var context = Open();
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var parameters = new SqlParameters();
        var ids = string.Join(", ", suggestion.Panels.Select(panel => panel.TargetId).Distinct().Select(id => parameters.Add(id)));

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT i.resolved_target_id, CAST(json_extract(i.raw_headers, '$.OBJECT') AS TEXT), i.session_date,
                   i.panel_label, i.filter_used, count(*), total(i.exposure_time)
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly}
              AND i.session_date IS NOT NULL
              AND i.panel_label IS NOT NULL
              AND i.resolved_target_id IN ({ids})
            GROUP BY 1, 2, 3, 4, 5
            """;
        parameters.ApplyTo(command);

        var frames = new List<(Guid Target, string Object, DateOnly Night, string Label, string? Filter, int Frames, double Seconds)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (SqlReaders.ReadDate(reader, 2) is { } night)
                {
                    frames.Add((reader.GetGuid(0), SqlReaders.ReadText(reader, 1) ?? "", night, reader.GetString(3),
                        SqlReaders.ReadText(reader, 4), SqlReaders.ReadInt(reader, 5), SqlReaders.ReadDouble(reader, 6)));
                }
            }
        }

        var map = aliases.Current;
        return [.. suggestion.Panels.SelectMany(entry => frames
            .Where(frame => frame.Target == entry.TargetId && SameLabel(frame.Label, entry.Label))
            .GroupBy(frame => (frame.Object, frame.Night, Filter: map.CanonicalFilter(frame.Filter)))
            .OrderBy(group => group.Key.Object, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Night)
            .ThenBy(group => group.Key.Filter ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => new SuggestionSessionRow(
                entry.TargetId, entry.Label, group.Key.Object, group.Key.Night, group.Key.Filter,
                group.Sum(frame => frame.Frames), group.Sum(frame => frame.Seconds),
                entry.Dates.Contains(group.Key.Night))))];
    }

    /// <summary>The primary name of each of these targets, for the suggestion row's target links
    /// (spec 12.17). An id with no target row is absent.</summary>
    public IReadOnlyDictionary<Guid, string> TargetNames(IReadOnlyCollection<Guid> targetIds)
    {
        using var context = Open();
        return context.Targets.Where(target => targetIds.Contains(target.Id))
            .ToDictionary(target => target.Id, target => target.PrimaryName);
    }

    // ---- the pieces the reads share ------------------------------------------------------

    private static ILookup<TripleKey, FrameBucket> ByTriple(IEnumerable<FrameBucket> buckets)
        => buckets.ToLookup(bucket => TripleKey.Of(bucket.TargetId, bucket.Date, bucket.Label));

    private static (DateOnly? First, DateOnly? Last) Range(IReadOnlyCollection<FrameBucket> counted)
        => counted.Count == 0 ? (null, null) : (counted.Min(bucket => bucket.Date), counted.Max(bucket => bucket.Date));

    private static IReadOnlyList<string> Filters(IEnumerable<FrameBucket> counted, AliasMap map)
        => [.. counted.Select(bucket => map.CanonicalFilter(bucket.Filter))
            .OfType<string>()
            .Where(filter => filter.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static IReadOnlyDictionary<string, double> IntegrationByFilter(IEnumerable<FrameBucket> counted, AliasMap map)
        => counted.Where(bucket => map.CanonicalFilter(bucket.Filter) is { Length: > 0 })
            .GroupBy(bucket => map.CanonicalFilter(bucket.Filter)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(bucket => bucket.Seconds), StringComparer.OrdinalIgnoreCase);

    private static bool SameLabel(string? a, string? b) => TripleKey.Fold(a) == TripleKey.Fold(b);

    private GalactiLogContext Open() => new(GalactiLogContextOptions.Create(connectionString.Value));
}
