using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.CustomColumns;

/// <summary>
/// The one place a surface's custom column list is built (spec 12.15's "Where the cells appear").
/// Four surfaces choose their columns; they choose them here and nowhere else, so a fifth surface
/// added later cannot invent a fifth rule (design lesson 1). A unit that filters the definition
/// list itself is a review finding.
/// </summary>
/// <remarks>
/// Every member is pure and takes the full list, so a caller that filtered or reordered its input
/// cannot reach a different answer. Each sorts by <c>DisplayOrder</c>, then <c>CreatedAt</c>, then
/// <c>Id</c>, which is the repository's own order. Membership in <c>hiddenKeys</c> is
/// <see cref="StringComparer.Ordinal"/>, the same comparer <c>DisplaySettings.ColumnsFor</c> and
/// <c>ColumnPickerViewModel</c> already use.
/// </remarks>
public static class CustomColumnSet
{
    /// <summary>Spec 12.15's dashboard target row: every <c>target</c>-scope column
    /// <see cref="IsShown"/> answers true for, in display order. <paramref name="hiddenKeys"/> is
    /// <c>display.columns.dashboard_hidden</c>.</summary>
    public static IReadOnlyList<CustomColumnDefinition> DashboardRow(
        IReadOnlyList<CustomColumnDefinition> all, IReadOnlyList<string> hiddenKeys)
        => Ordered(all.Where(column =>
            column.Scope is CustomColumnScope.Target && IsShown(hiddenKeys, column.Slug)));

    /// <summary>Spec 12.15's dashboard night expander: every <c>session</c>-scope column, in
    /// display order. Ungated (user choice 4): the expander has no picker today and this phase does
    /// not give it one.</summary>
    public static IReadOnlyList<CustomColumnDefinition> NightExpander(
        IReadOnlyList<CustomColumnDefinition> all)
        => Ordered(all.Where(column => column.Scope is CustomColumnScope.Session));

    /// <summary>Spec 12.15's Nights ledger row: every <c>session</c>-scope column
    /// <see cref="IsShown"/> answers true for, in display order. <paramref name="hiddenKeys"/> is
    /// <c>display.columns.ledger_hidden</c>.</summary>
    public static IReadOnlyList<CustomColumnDefinition> LedgerRow(
        IReadOnlyList<CustomColumnDefinition> all, IReadOnlyList<string> hiddenKeys)
        => Ordered(all.Where(column =>
            column.Scope is CustomColumnScope.Session && IsShown(hiddenKeys, column.Slug)));

    /// <summary>Spec 12.15's session pane rig rows: every <c>rig</c>-scope column, in display
    /// order. Ungated.</summary>
    public static IReadOnlyList<CustomColumnDefinition> RigRow(
        IReadOnlyList<CustomColumnDefinition> all)
        => Ordered(all.Where(column => column.Scope is CustomColumnScope.Rig));

    /// <summary>Spec 12.17's mosaics table and the mosaic detail header (Phase 18): every
    /// mosaic-scope column in display order. The table filters this by the stored visible list; the
    /// detail header shows them all.</summary>
    public static IReadOnlyList<CustomColumnDefinition> MosaicRow(
        IReadOnlyList<CustomColumnDefinition> all)
        => Ordered(all.Where(column => column.Scope is CustomColumnScope.Mosaic));

    /// <summary>True while any <c>rig</c>-scope column exists. What makes the session pane draw a
    /// rig label row on a single-rig night (user choice 7, spec 12.15, spec 12.4 item 2 as
    /// amended), and therefore what keeps every existing session pane pin unmoved on a library
    /// that has no such column.</summary>
    public static bool AnyRigScope(IReadOnlyList<CustomColumnDefinition> all)
        => all.Any(column => column.Scope is CustomColumnScope.Rig);

    /// <summary>The one default for a gated custom column, read by both surfaces and both pickers:
    /// shown unless the user switched it off, so a new column starts shown and a hidden slug stays
    /// hidden. <paramref name="hiddenKeys"/> is a <c>*_hidden</c> entry of
    /// <c>display.columns</c>.</summary>
    public static bool IsShown(IReadOnlyList<string> hiddenKeys, string slug)
        => !hiddenKeys.Contains(slug, StringComparer.Ordinal);

    private static IReadOnlyList<CustomColumnDefinition> Ordered(
        IEnumerable<CustomColumnDefinition> columns)
        => [.. columns
            .OrderBy(column => column.DisplayOrder)
            .ThenBy(column => column.CreatedAt)
            .ThenBy(column => column.Id)];
}
