using Avalonia.Controls;

namespace GalactiLog.App.Controls;

/// <summary>
/// Spec 12.4's raw header panel (spec 4.3 puts it in <c>Controls</c>). All behaviour is in
/// <see cref="ViewModels.TargetDetail.RawHeaderPanelViewModel"/>; this class exists so the markup
/// has a partial to compile into. Takes an image id and nothing else through its view-model, which
/// is what makes Phase 8's preview modal reuse of this control free.
/// </summary>
public partial class RawHeaderPanel : UserControl
{
    public RawHeaderPanel() => InitializeComponent();
}
