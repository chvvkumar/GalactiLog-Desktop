using Avalonia.Controls;

namespace GalactiLog.App.Views.Mosaics;

/// <summary>
/// Spec 12.17's Mosaics page, bound to <see cref="ViewModels.Mosaics.MosaicsPageViewModel"/>. All
/// behaviour is in the view-model; this class exists so the markup has a partial to compile into.
/// </summary>
public partial class MosaicsView : UserControl
{
    public MosaicsView() => InitializeComponent();
}
