using Avalonia.Controls;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's stats card, bound to <see cref="ViewModels.Analysis.StatsCardViewModel"/>. All
/// formatting is the view-model's; this class exists so the markup has a partial to compile into.
/// </summary>
public partial class StatsCardView : UserControl
{
    public StatsCardView() => InitializeComponent();
}
