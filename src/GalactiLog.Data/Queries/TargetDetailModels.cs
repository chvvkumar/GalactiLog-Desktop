namespace GalactiLog.Data.Queries;

/// <summary>One catalog membership badge (spec 5.4, 9.8).</summary>
public sealed record CatalogMembershipBadge(string CatalogName, string CatalogNumber, string? Metadata);

/// <summary>Spec 12.4's header block. Every field is null or empty for an <c>obj:</c> group,
/// which has no targets row at all; <see cref="PrimaryName"/> is then the raw OBJECT string
/// (or "Uncategorized").</summary>
/// <param name="ReferenceArcsecPerPixel">The plate scale of the target's most recent LIGHT frame
/// by capture date, which is the frame spec 11.4's reference thumbnail pass prefers and the frame
/// "Reveal folder" resolves to. Null when that frame carries no <c>arcsec_per_pixel</c>, and null
/// for an <c>obj:</c> group, which has no reference thumbnail to measure over.</param>
/// <param name="ReferenceFrameWidthPixels">That frame's <c>NAXIS1</c>, read out of its stored
/// <c>raw_headers</c> document (spec 6.1.2), because <c>images</c> stores no width column. With
/// the plate scale above it gives the frame's field of view, which is what turns a scale bar over
/// the reference thumbnail into a measurement. Null when the header is absent or not an integer.
/// </param>
public sealed record TargetHeaderBlock(
    string GroupKey,
    Guid? TargetId,
    string PrimaryName,
    IReadOnlyList<string> Aliases,
    string? ObjectType,
    string ObjectCategory,
    string? Constellation,
    double? Ra,
    double? Dec,
    double? SizeMajor,
    double? SizeMinor,
    double? PositionAngle,
    double? VMag,
    double? SurfaceBrightness,
    string? SacDescription,
    string? SacNotes,
    string? Notes,
    string? ReferenceThumbnailPath,
    double? ReferenceArcsecPerPixel,
    int? ReferenceFrameWidthPixels,
    bool NameLocked,
    bool UserDefined,
    IReadOnlyList<CatalogMembershipBadge> CatalogMemberships);

/// <summary>Spec 12.4's totals row. "Avg" is a mean over every LIGHT frame of the group;
/// the session cards use medians (see <see cref="GalactiLog.Core.Metrics.Statistics"/>).</summary>
/// <param name="HfrArcsecExcludedCount">Frames carrying an HFR but no plate scale, excluded from
/// <see cref="AvgHfrArcsec"/>. Spec 12.4: "the count of frames excluded for lack of one is
/// shown."</param>
/// <param name="EccentricityModalSource">The modal <c>eccentricity_source</c> the average pooled,
/// shown beside the figure so the disclosure names the source. Null in two cases, and the view
/// must render both: no frame carries an eccentricity at all (<see cref="AvgEccentricity"/> is
/// then null too), or the winning source is itself null because the frames carry an eccentricity
/// with no recorded source (<see cref="AvgEccentricity"/> is then a real figure whose source is
/// unknown).</param>
/// <param name="EccentricityExcludedCount">Frames carrying an eccentricity from a non-modal
/// source, excluded from <see cref="AvgEccentricity"/>.</param>
/// <param name="IntegrationSecondsByFilter">Exposure seconds per canonical filter name over the same frames <see cref="IntegrationSeconds"/> sums. A frame with no filter
/// is in no bucket but still in the total.</param>
public sealed record TargetTotals(
    double IntegrationSeconds,
    int FrameCount,
    int SessionCount,
    DateOnly? FirstSessionDate,
    DateOnly? LastSessionDate,
    double? AvgHfr,
    double? AvgHfrArcsec,
    int HfrArcsecExcludedCount,
    double? AvgEccentricity,
    string? EccentricityModalSource,
    int EccentricityExcludedCount,
    double? AvgFwhm,
    double? AvgGuidingRmsArcsec,
    double? AvgDetectedStars,
    IReadOnlyList<string> FiltersUsed,
    IReadOnlyDictionary<string, double> IntegrationSecondsByFilter,
    IReadOnlyList<string> Equipment);

/// <summary>Spec 12.4's guiding provenance truth table (Phase 15A), over one night's RMS-bearing
/// frames (review ruling: a frame with no <c>guiding_rms_arcsec</c> at all never counts, whatever
/// its source column holds; a real RMS with a null source counts as not from a guide log). A
/// four-valued token rather than two booleans: <see cref="None"/> and <see cref="Csv"/> both draw
/// no dagger, and only a named third and fourth state keep a caller from combining two booleans
/// into the wrong one. Computed once by <c>TargetDetailQuery</c> and carried on
/// <see cref="SessionOverview"/>, so the session pane's facts line and the ledger cell's mark read
/// the same value and cannot disagree about a night.</summary>
public enum GuidingRmsProvenance
{
    /// <summary>No frame of the night carries a guiding RMS at all. Draws nothing at all, not an
    /// empty mark, the same rule the night strip follows for a null metric.</summary>
    None,

    /// <summary>At least one frame carries a guiding RMS, and none of those RMS-bearing frames is
    /// from a PHD2 guide log: each is <c>csv</c>, a null source, or an unrecognized string. No
    /// dagger, because none of the night's figures was measured somewhere other than beside the
    /// frame (the common case is the CSV sidecar, which is why the name stayed <c>Csv</c>, but the
    /// value does not require every source to literally read <c>csv</c>).</summary>
    Csv,

    /// <summary>Every RMS-bearing frame of the night is <c>phd2</c>. Dagger, and the facts line
    /// carries the unqualified "from a PHD2 guide log".</summary>
    Phd2,

    /// <summary>The night's RMS-bearing frames are a mix: at least one is <c>phd2</c> and at least
    /// one is not (<c>csv</c>, null, or unrecognized). Dagger, and the facts line carries "from a
    /// PHD2 guide log for some frames", because the unqualified sentence would claim the whole
    /// median came from the log when it did not.</summary>
    Mixed,
}

/// <summary>One night of spec 12.4, as the ledger's night row reads it and the session pane opens
/// from (P12 R10 retired the collapsed accordion card this described). Medians, not means.
/// </summary>
/// <param name="Camera">The canonical camera of the session's first frame by capture time,
/// matching the web application's <c>SessionOverview.camera</c>. A multi-rig night reports the
/// first frame's rig and discloses the rest through <see cref="RigCount"/>.</param>
/// <param name="RigCount">Distinct canonical (telescope, camera) pairs on the night. The query
/// still reports it and no P12 surface renders it: the comp's ledger row carries no rig marker, so
/// the view model's multi-rig flag and its count line went with the accordion card.</param>
/// <param name="HasNotes">A <c>session_notes</c> row exists for (target, date). Drives the notes
/// indicator. Always false for an <c>obj:</c> group, which has no target id to key on.</param>
/// <param name="EccentricitySource">The modal <c>eccentricity_source</c> of this session, the
/// only source <see cref="MedianEccentricity"/> pooled (spec 7.2, review ruling). Null on the
/// same two conditions as <c>TargetTotals.EccentricityModalSource</c>, and it can differ from the
/// target's modal source, which is why the card labels its own.</param>
/// <param name="GuidingProvenance">Spec 12.4's guiding provenance truth table (Phase 15A), over
/// this night's frames. Appended at the end so the one existing construction site in <c>src</c>
/// and the one in test support both keep their positional order.</param>
/// <param name="GuidingSessionCount">How many PHD2 guiding sessions the Guiding band will show for
/// this card, so a CLOSED band can state the count without opening anything and issuing a query
/// (spec 12.4, task3-review.md P2-2). Zero means none, which is also what a library with no guide
/// log reports.
/// <para>
/// It is the count the band itself would report and not a wider one: the same live-map rig rule
/// and the same alias-expanded selection <c>Phd2NightQuery.Get</c> applies, narrowed to this
/// card's rig when <paramref name="RigCount"/> is one and unfiltered when it is more, which is the
/// argument <c>SessionCardViewModel</c> passes the section. A count taken over the whole night
/// instead would disagree with the opened band on exactly the two-rig nights the band exists to
/// disambiguate.
/// </para>
/// <para>
/// Trailing and defaulted so the existing positional construction sites keep compiling, which is
/// the same reason <paramref name="GuidingProvenance"/> was appended.
/// </para></param>
public sealed record SessionOverview(
    DateOnly SessionDate,
    double IntegrationSeconds,
    int FrameCount,
    double? MedianHfr,
    double? MedianHfrArcsec,
    int HfrArcsecExcludedCount,
    double? MedianEccentricity,
    string? EccentricitySource,
    double? MedianFwhm,
    double? MedianGuidingRmsArcsec,
    double? MedianDetectedStars,
    IReadOnlyList<string> FiltersUsed,
    string? Camera,
    string? Telescope,
    int RigCount,
    bool HasNotes,
    GuidingRmsProvenance GuidingProvenance,
    int GuidingSessionCount = 0);

/// <summary>Everything <c>TargetDetailQuery</c> returns in one round trip.</summary>
/// <param name="Sessions">Newest session first (spec 12.4: "one per session date, newest
/// first").</param>
/// <param name="FramePaths">Absolute paths of every LIGHT frame in the group, in capture order,
/// for spec 12.4's "copy frame list to clipboard" action (Task 3). Free here: the query already
/// reads one row per frame, and adding one text column avoids a second full pass at click time.
/// Never used to open, write, or touch a file: <c>ShellIntegration</c> is the only caller and it
/// copies text.</param>
public sealed record TargetDetail(
    TargetHeaderBlock Header,
    TargetTotals Totals,
    IReadOnlyList<SessionOverview> Sessions,
    IReadOnlyList<string> FramePaths)
{
    /// <summary>One row per night and canonical filter, nights newest first, filters in
    /// name order.</summary>
    public IReadOnlyList<NightFilterOverview> NightFilters { get; init; } = [];

    /// <summary>Every dated frame with a capture time, in capture order.</summary>
    public IReadOnlyList<NightFramePoint> NightFrames { get; init; } = [];
}

/// <summary>Frames of one exposure length.</summary>
public sealed record ExposureCount(double Seconds, int Frames);

/// <summary>One night and one canonical filter. <see cref="Filter"/> is the key
/// <see cref="TargetTotals.IntegrationSecondsByFilter"/> uses; the medians are in the units of
/// <see cref="SessionOverview"/>, and eccentricity pools the night's modal source only.</summary>
public sealed record NightFilterOverview(
    DateOnly SessionDate,
    string Filter,
    double IntegrationSeconds,
    int FrameCount,
    IReadOnlyList<ExposureCount> Exposures,
    double? MedianHfr,
    double? MedianEccentricity,
    double? MedianFwhm,
    double? MedianGuidingRms,
    double? MedianDetectedStars);

/// <summary>One frame as a Compare nights lane plots it. <see cref="Filter"/> is empty for
/// a frame with no filter; <see cref="Eccentricity"/> is null unless the frame is in its night's
/// modal source pool.</summary>
public sealed record NightFramePoint(
    DateOnly SessionDate,
    DateTime CaptureUtc,
    string Filter,
    double? Hfr,
    double? Eccentricity,
    double? Fwhm,
    double? GuidingRms,
    double? DetectedStars);
