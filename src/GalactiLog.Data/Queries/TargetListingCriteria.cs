namespace GalactiLog.Data.Queries;

/// <summary>Sort keys of the dashboard target list (spec 12.2).</summary>
public enum TargetListingSort
{
    Name,
    Integration,
    Frames,
    Sessions,
    LastSession,
    Equipment,
}

/// <summary>One metric's bounds. Either bound may be unset; only the bound the user actually
/// set contributes a clause (spec 12.2.1).</summary>
public readonly record struct MetricRange(double? Min, double? Max)
{
    public bool IsSet => Min is not null || Max is not null;
}

// Task 3 fills this in (spec 12.3): key validation against ^[A-Za-z0-9_-]{1,20}$, the bound
// json_extract path, the numeric parse for the ordering operators, and the LIKE escape for
// "contains". Declared here so the criteria shape does not change when Task 3 lands.
public sealed record HeaderCondition(string Key, string Operator, string Value);

/// <summary>Metric key to <c>images</c> column, verbatim from spec 12.2.1. A key that is not in
/// this map is dropped, exactly like an invalid header key: the column name is never taken from
/// user text.</summary>
public static class MetricColumns
{
    public static readonly IReadOnlyDictionary<string, string> ByKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hfr"] = "median_hfr",
            // spec 7.1.1: the Metrics Quality "fwhm" box maps to fwhm, never median_fwhm.
            ["fwhm"] = "fwhm",
            ["eccentricity"] = "eccentricity",
            ["stars"] = "detected_stars",
            ["guiding_rms"] = "guiding_rms_arcsec",
            ["adu_mean"] = "adu_mean",
            ["focuser_temp"] = "focuser_temp",
            ["ambient_temp"] = "ambient_temp",
            ["humidity"] = "humidity",
            ["airmass"] = "airmass",
        };
}

/// <summary>Everything the dashboard filter panel can constrain (spec 12.2). Immutable; the
/// view-model builds a new one per change.</summary>
public sealed record TargetListingCriteria
{
    /// <summary>The display category of a frame with no resolved target (spec 9.8).</summary>
    public const string UnresolvedCategory = "Unresolved";

    /// <summary>The synthetic OBJECT value every LIGHT frame with an empty, missing or unparseable
    /// OBJECT collapses to (spec 12.2 step 2). Public (F2) so the search dropdown can pin that
    /// group through <see cref="UnresolvedObject"/> like any other unresolved name.</summary>
    public const string UncategorizedObject = "__uncategorized__";

    // Pinned selections from the search box (Task 8). Exactly one of these may be set; setting
    // both simply yields an empty result rather than an error.
    public Guid? TargetId { get; init; }

    /// <summary>The raw OBJECT string, without the "obj:" prefix.</summary>
    public string? UnresolvedObject { get; init; }

    /// <summary>Spec 9.8 display categories, plus <see cref="UnresolvedCategory"/>.</summary>
    public IReadOnlyList<string> ObjectCategories { get; init; } = [];

    public DateOnly? SessionDateFrom { get; init; }
    public DateOnly? SessionDateTo { get; init; }

    /// <summary>Canonical filter names; expanded through the alias map before matching.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];

    public string? Camera { get; init; }
    public string? Telescope { get; init; }

    /// <summary>Metric key (spec 12.2.1) to bounds. Only entries with a bound set contribute.</summary>
    public IReadOnlyDictionary<string, MetricRange> MetricRanges { get; init; }
        = new Dictionary<string, MetricRange>();

    /// <summary>Raw FITS header conditions. Unused in Task 2; Task 3 gives it meaning.</summary>
    public IReadOnlyList<HeaderCondition> HeaderConditions { get; init; } = [];

    /// <summary>Spec 12.15's custom filters, each already resolved to a clause-bearing mode. A
    /// filter whose column no longer exists is dropped by the query, silently, the way an unknown
    /// metric key already is.</summary>
    public IReadOnlyList<CustomColumnFilter> CustomFilters { get; init; } = [];

    public TargetListingSort Sort { get; init; } = TargetListingSort.LastSession;
    public bool Descending { get; init; } = true;

    /// <summary>1-based. Clamped to at least 1 by the query.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Defaults to spec 5.8.1's general.default_page_size default. Clamped to at least
    /// 1 by the query.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>True when any filter section holds a value; drives the summary strip's
    /// "filtered" marker and the choice between the two empty states of spec 12.10.
    /// <para>
    /// F3: this must agree with the SQL exactly. A blank <see cref="Filters"/> or
    /// <see cref="ObjectCategories"/> entry contributes no clause, and a metric key that is not in
    /// <see cref="MetricColumns.ByKey"/> is dropped like an invalid header key, so neither may
    /// report a filter as active. Otherwise an empty library shows "No targets match these filters"
    /// with a Reset button that clears nothing, instead of "No frames catalogued yet".
    /// </para></summary>
    public bool AnyFilterActive =>
        TargetId is not null
        || !string.IsNullOrWhiteSpace(UnresolvedObject)
        || ObjectCategories.Any(category => !string.IsNullOrWhiteSpace(category))
        || SessionDateFrom is not null
        || SessionDateTo is not null
        || Filters.Any(filter => !string.IsNullOrWhiteSpace(filter))
        || !string.IsNullOrWhiteSpace(Camera)
        || !string.IsNullOrWhiteSpace(Telescope)
        || MetricRanges.Any(entry => entry.Value.IsSet && MetricColumns.ByKey.ContainsKey(entry.Key))
        || HeaderConditions.Count > 0
        || CustomFilters.Count > 0;
}
