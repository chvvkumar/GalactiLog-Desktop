using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace GalactiLog.App.Views.TargetDetail.Parts;

/// <summary>The collapsible "Night metrics" section: the per-filter table, the ranges, the
/// comparison line and the sharpest frame in one wrapping row under the night header.</summary>
public partial class NightMetricsSection : Expander
{
    public NightMetricsSection() => InitializeComponent();

    // The subclass keeps the Expander's own control theme.
    protected override Type StyleKeyOverride => typeof(Expander);

    /// <summary>The header line's height: the only part of the section the layout charges to the
    /// lanes floors, so opening the section scrolls the lanes rather than taking frame rows.</summary>
    public double HeaderHeight
        => this.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault()?.Bounds.Height ?? Bounds.Height;
}
