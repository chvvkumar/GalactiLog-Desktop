using Avalonia.Controls;

namespace GalactiLog.App.Views.TargetDetail.Wbpp;

/// <summary>
/// design-spec 12.13's quality filter panel: the chips toolbar and the verdict table. Markup only.
/// </summary>
/// <remarks>
/// This file exists because <c>x:Class</c> needs a C# partial to generate
/// <c>InitializeComponent</c> into, and for no other reason: the panel does nothing a view-model
/// cannot. There is no measurement, no scroll-into-view and no control-only collection to hand
/// over, which are the three things <c>FrameTableView</c>'s own code-behind is for.
/// </remarks>
public partial class QualityPanelView : UserControl
{
    public QualityPanelView() => InitializeComponent();
}
