using Avalonia.Controls;
using GalactiLog.App.Views.TargetDetail.Parts;

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

    public static void Apply(Control column, NightsLedgerPart ledger, double? width, bool collapsed)
    {
        var form = FormOf(width, collapsed);
        column.Width = form == SidebarForm.Collapsed || width is null ? double.NaN : width.Value;
        column.MinWidth = form == SidebarForm.Wide ? WideMinWidth : 0d;
        ledger.IsCompact = form != SidebarForm.Wide;
        ledger.IsCollapsed = form == SidebarForm.Collapsed;
    }
}
