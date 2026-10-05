namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One entry of <c>scan_filters.include_paths</c> or <c>scan_filters.exclude_paths</c>
/// (design-spec 5.8.1, 10.2). Both lists have the same shape and the same validation rule, so
/// they share one row type and differ only in <see cref="IsExclude"/>, which selects the
/// message wording.
/// </summary>
/// <param name="path">The absolute path.</param>
/// <param name="isExclude">True for an exclude path, false for an include path.</param>
public sealed class FilterPathRowViewModel(string path, bool isExclude) : PathRowViewModel(path)
{
    /// <summary>Which of the two lists this row belongs to. Only the error message wording and
    /// the remove command differ.</summary>
    public bool IsExclude { get; } = isExclude;

    /// <summary>The settings field this row writes, for the inline error message, matching the
    /// field names <c>ScanFilterConfig.Validate</c> itself uses.</summary>
    public string FieldName => IsExclude ? "scan_filters.exclude_paths" : "scan_filters.include_paths";
}
