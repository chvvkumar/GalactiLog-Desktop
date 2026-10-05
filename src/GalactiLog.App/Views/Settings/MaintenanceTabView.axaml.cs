using Avalonia.Controls;

namespace GalactiLog.App.Views.Settings;

/// <summary>Design-spec 12.7's Maintenance tab. No code-behind handlers: every card is data and
/// every button carries a command the view-model built, and the typed confirmation is a modal of
/// its own opened through <c>ModalHost</c>.</summary>
public partial class MaintenanceTabView : UserControl
{
    public MaintenanceTabView() => InitializeComponent();
}
