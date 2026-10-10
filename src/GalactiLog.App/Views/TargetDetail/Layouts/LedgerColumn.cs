using Avalonia.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

/// <summary>The width rule of the sidebar column that holds the Nights list. The list keeps its own
/// width and the column shows its left part, from the date's right edge (the stop) to the table's
/// own width (open); the night pane covers the rest (spec.md, Nights list).</summary>
public static class LedgerColumn
{
    /// <summary>The column's width between the stop and open: nothing stored is fully open, and
    /// collapsed is the stop.</summary>
    public static double WidthOf(double? width, bool collapsed, double stop, double open)
        => collapsed ? stop : Math.Clamp(width ?? open, stop, Math.Max(stop, open));

    /// <summary>A released width: at the stop it stores collapsed and keeps the last open width for
    /// the chevron; at open it stores fully open (null), which follows the open width when a custom
    /// column is added later.</summary>
    public static TargetLayoutState Committed(TargetLayoutState state, double width, double stop, double open)
        => width <= stop + 0.5 ? state with { SidebarCollapsed = true }
            : width >= open - 0.5 ? state with { SidebarWidth = null, SidebarCollapsed = false }
            : state with { SidebarWidth = width, SidebarCollapsed = false };

    // The list's vertical bar is always drawn at the column's right edge, so both edges carry it.
    public static double StopOf(NightsLedgerPart ledger) => ledger.CollapsedWidth + TableMetrics.ScrollBarSize;

    /// <summary>Open, capped at <paramref name="room"/> (ruling R7): extra custom columns stay
    /// covered rather than squeezing the night pane.</summary>
    public static double OpenOf(NightsLedgerPart ledger, double room)
        => Math.Max(StopOf(ledger), Math.Min(ledger.OpenWidth + TableMetrics.ScrollBarSize, room));

    public static void Apply(Control column, NightsLedgerPart ledger, double? width, bool collapsed, double room)
    {
        // Unmeasured: the first layout pass reports the extents, which applies again.
        if (ledger.OpenWidth <= 0d)
        {
            column.Width = double.NaN;
            ledger.IsCollapsed = collapsed;
            ledger.IsStacked = collapsed;
            return;
        }

        var stop = StopOf(ledger);
        var open = OpenOf(ledger, room);
        var applied = WidthOf(width, collapsed, stop, open);
        column.Width = applied;
        // With no travel the list keeps its open form: the title line (ruling R8) is measured only
        // in that form, so a list collapsed before it was ever measured would never open.
        ledger.IsCollapsed = applied <= stop + 0.5 && open > stop + 0.5;
        // The chrome sits outside the slide-over, so it reflows to the column: narrower than the
        // title line, it stacks as it does collapsed, or the title draws under the actions.
        ledger.IsStacked = ledger.IsCollapsed || applied < ledger.TitleLineWidth + TableMetrics.ScrollBarSize - 0.5;
    }
}
