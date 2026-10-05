using System.Text.Json.Serialization;

namespace GalactiLog.Core.Mosaics;

/// <summary>One LIGHT frame as detection reads it (spec 7.7): the stored geometry columns of
/// section 5.2 and the raw <c>OBJECT</c>. A target's frames are passed in <c>capture_date</c>, then
/// <c>id</c> order, which is what "the earliest frame that carries a field of view" means.</summary>
/// <param name="SessionDate">The frame's night; a null night contributes no night.</param>
/// <param name="ObjectName">The raw <c>OBJECT</c> header value (ruling R19a): panels of one target
/// are told apart by it.</param>
public sealed record DetectionFrame(
    DateOnly? SessionDate, double? RaDeg, double? DecDeg, double? ArcsecPerPixel, int? WidthPx,
    double ExposureSeconds, string? Filter, string? ObjectName);

/// <summary>One unmerged target and its LIGHT frames (spec 7.7, candidates).</summary>
public sealed record DetectionTarget(Guid Id, string PrimaryName, IReadOnlyList<DetectionFrame> Frames);

/// <summary>The three <c>general.mosaic_*</c> settings detection reads (spec 7.7).</summary>
public sealed record DetectionSettings(IReadOnlyList<string> Keywords, int CampaignGapDays, double PositionToleranceArcmin);

/// <summary>A dismissed signature and the union of its rejected rows' nights (spec 7.7, writing
/// suggestions, step 1).</summary>
public sealed record DismissedSignature(string Signature, IReadOnlySet<DateOnly> Dates);

/// <summary>One entry of a suggestion (ruling R19a): the target, its panel label, the web's
/// <c>OBJECT</c> pattern and the campaign's nights of the frames whose <c>OBJECT</c> yields this
/// panel, sorted.</summary>
public sealed record SuggestionPanel(Guid TargetId, string Label, string Pattern, IReadOnlyList<DateOnly> Dates);

/// <summary>One entry of <see cref="SuggestionGeometry.Panels"/> (spec 7.7, geometry).</summary>
public sealed record GeometryPanel(
    [property: JsonPropertyName("target_id")] Guid TargetId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("ra")] double? Ra,
    [property: JsonPropertyName("dec")] double? Dec);

/// <summary><c>mosaic_suggestions.geometry</c> (spec 5.25, 7.7): ra and dec rounded to 6 decimals,
/// pitches in arcminutes rounded to 3, the median field of view rounded to 2.</summary>
public sealed record SuggestionGeometry(
    [property: JsonPropertyName("panels")] IReadOnlyList<GeometryPanel> Panels,
    [property: JsonPropertyName("pitches")] IReadOnlyList<double> Pitches,
    [property: JsonPropertyName("fov_arcmin")] double? FovArcmin);

/// <summary>One suggestion detection would insert as <c>pending</c> (spec 5.25, 7.7).</summary>
/// <param name="Confidence"><c>high</c> or <c>low</c>.</param>
/// <param name="DiscoverySource"><c>name</c>, <c>position</c> or <c>both</c>.</param>
/// <param name="Geometry">Always built; null ra and dec on an entry without a position.</param>
public sealed record SuggestionCandidate(
    string SuggestedName, string BaseName, IReadOnlyList<SuggestionPanel> Panels, string Confidence,
    string DiscoverySource, SuggestionGeometry Geometry, IReadOnlyList<string> Flags, string DedupSignature)
{
    /// <summary>The entries' target ids in entry order, a target once per panel.</summary>
    public IReadOnlyList<Guid> TargetIds => [.. Panels.Select(p => p.TargetId)];

    /// <summary>The entries' labels in entry order; a label may repeat.</summary>
    public IReadOnlyList<string> PanelLabels => [.. Panels.Select(p => p.Label)];
}
