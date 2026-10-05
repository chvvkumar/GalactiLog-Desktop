using Avalonia.Controls;

namespace GalactiLog.App.Views;

/// <summary>
/// Spec 12.6's Activity page, bound to
/// <see cref="ViewModels.Activity.ActivityViewModel"/>. All behaviour is in the view-model,
/// including the two expanders, which are per-row commands; this class exists so the markup has a
/// partial to compile into.
/// </summary>
public partial class ActivityView : UserControl
{
    public ActivityView() => InitializeComponent();
}
