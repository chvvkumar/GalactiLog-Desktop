using GalactiLog.Core.Settings;

namespace GalactiLog.App.ViewModels.CustomColumns;

/// <summary>
/// The one per-kind cell width table (spec 12.15). Every surface that draws a custom cell sizes it
/// from here: the dashboard row and its header strip through <c>TargetListView</c>, the Nights
/// ledger's night rows and header strip through <c>TargetDetailViewModel</c>.
/// </summary>
/// <remarks>
/// <para>
/// It lives beside the cell rather than in either view, because the figures were written out twice
/// and had already drifted: the ledger took 120 for a text cell and the dashboard took 160, and the
/// heading measurement that a long column name needs landed in one of the two copies only. Each
/// surface keeps its own cap and its own drop rule, which really are different (the ledger's ceiling
/// comes from the session pane's minimum, the dashboard's from the row's fit width); what a kind's
/// editor needs is one answer and is chosen here.
/// </para>
/// <para>
/// The two surfaces' own names for these figures are aliases of these constants, so the markup that
/// binds them with <c>x:Static</c> and the arithmetic that sums them keep reading one table.
/// </para>
/// </remarks>
public static class CustomCellWidths
{
    /// <summary>A check box cell. An empty <c>CheckBox</c> measures 18, plus the cell's two 8 pixel
    /// gutters.</summary>
    public const double Check = 36d;

    /// <summary>A dropdown cell: the widest option plus the combo's chevron, bounded so one long
    /// option cannot take the row.</summary>
    public const double Choice = 120d;

    /// <summary>
    /// A text cell. 120 rather than the 160 a text-bearing dashboard cell otherwise carries: the
    /// wide ledger's whole custom strip is capped by the session pane's own minimum, and at 160 a
    /// text column and a check box together do not fit under that cap. 120 is what a dropdown cell
    /// takes and is enough for the tag a session-scope text column holds.
    /// </summary>
    public const double Text = 120d;

    /// <summary>The width one cell's editor needs, from the column's type and from nothing else.
    /// </summary>
    public static double For(CustomColumnType type) => type switch
    {
        CustomColumnType.Boolean => Check,
        CustomColumnType.Dropdown => Choice,
        _ => Text,
    };
}
