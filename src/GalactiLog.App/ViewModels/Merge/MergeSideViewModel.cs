using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Merge;

/// <summary>
/// One column of spec 12.9's side-by-side comparison: primary name, catalog id, aliases, object
/// type, RA and Dec, frame count, integration, session count, and first and last session.
/// </summary>
/// <remarks>
/// Formatting only, in the shape <c>TargetHeaderViewModel</c> and <c>SearchResultViewModel</c>
/// already established: no queries, no business logic, and every field with a <c>Has*</c>
/// companion so the view binds <c>IsVisible</c> and needs no converters. The coordinate
/// formatters are <c>TargetHeaderViewModel</c>'s own and the date and count formatters are
/// <c>MetricText</c>'s, so the dialog cannot disagree with Target detail about the same figures
/// (collision map, designated owners).
/// </remarks>
public sealed class MergeSideViewModel(MergePreviewSide side)
{
    /// <summary>The read model behind the column.</summary>
    public MergePreviewSide Side { get; } = side;

    /// <summary>Null for an unresolved <c>OBJECT</c> loser, which has no <c>targets</c> row and
    /// therefore cannot become the winner.</summary>
    public Guid? TargetId => Side.TargetId;

    public bool IsUnresolvedName => Side.TargetId is null;

    public string Name => Side.PrimaryName;

    public string CatalogId => Side.CatalogId ?? "";

    public bool HasCatalogId => CatalogId.Length > 0;

    /// <summary>The alias array as one line. An unresolved name has none.</summary>
    public string AliasesText => string.Join(", ", Side.Aliases);

    public bool HasAliases => Side.Aliases.Count > 0;

    /// <summary>SIMBAD's raw OTYPELIST (spec 9.8), shown verbatim beside its display
    /// category.</summary>
    public string ObjectType => Side.ObjectType ?? "";

    public bool HasObjectType => ObjectType.Length > 0;

    /// <summary>Spec 9.8's display category, never empty: an unresolved name reports
    /// <c>Unresolved</c>.</summary>
    public string ObjectCategory => Side.ObjectCategory;

    /// <summary>Right ascension as <c>HH:mm:ss.s</c>, from <c>TargetHeaderViewModel</c>'s own
    /// formatter.</summary>
    public string RaText { get; } = TargetHeaderViewModel.FormatRightAscension(side.Ra);

    public bool HasRa => RaText.Length > 0;

    /// <summary>Declination as <c>+DD:MM:SS</c>, from the same formatter.</summary>
    public string DecText { get; } = TargetHeaderViewModel.FormatDeclination(side.Dec);

    public bool HasDec => DecText.Length > 0;

    /// <summary>LIGHT frames only, which is what every other aggregate on the page set
    /// counts.</summary>
    public string FrameCountText => MetricText.Count(Side.FrameCount);

    public string IntegrationText => MetricText.Hours(Side.IntegrationSeconds);

    /// <summary>Dated sessions (spec 12.4): a frame with no <c>session_date</c> belongs to no
    /// session.</summary>
    public string SessionCountText => MetricText.Count(Side.SessionCount);

    public string FirstSessionText => Side.FirstSession is { } date ? MetricText.Date(date) : "";

    public bool HasFirstSession => FirstSessionText.Length > 0;

    public string LastSessionText => Side.LastSession is { } date ? MetricText.Date(date) : "";

    public bool HasLastSession => LastSessionText.Length > 0;
}
