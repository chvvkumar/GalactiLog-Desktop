using Avalonia.Controls;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

/// <summary>The sidebar's three forms, picked from its width alone: wide from
/// <see cref="WideMinWidth"/>, compact from <see cref="CompactWidth"/>, collapsed below it.</summary>
public enum SidebarForm { Wide, Compact, Collapsed }

/// <summary>The width rule of the column that holds the nights ledger: the form from the dragged
/// width, the column at that width, or at the ledger's own width when nothing is stored or it is
/// collapsed.</summary>
public static class LedgerColumn
{
    public const double CompactWidth = 472d;

    public const double WideMinWidth = 520d;

    public static SidebarForm FormOf(double? width, bool collapsed)
        => collapsed || width < CompactWidth ? SidebarForm.Collapsed
            : width < WideMinWidth ? SidebarForm.Compact
            : SidebarForm.Wide;

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

    public static void Apply(Control column, NightsLedgerPart ledger, double? width, bool collapsed)
    {
        var form = FormOf(width, collapsed);
        column.Width = form == SidebarForm.Collapsed || width is null ? double.NaN : width.Value;
        column.MinWidth = form == SidebarForm.Wide ? WideMinWidth : 0d;
        ledger.IsCompact = form != SidebarForm.Wide;
        ledger.IsCollapsed = form == SidebarForm.Collapsed;
    }
}
