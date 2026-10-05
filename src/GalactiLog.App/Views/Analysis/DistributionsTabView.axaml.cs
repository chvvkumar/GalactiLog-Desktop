using Avalonia.Controls;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's Distributions tab body, bound to
/// <see cref="ViewModels.Analysis.DistributionsTabViewModel"/>. All the behaviour is on the
/// view-model; this class exists so the markup has a partial to compile into.
/// </summary>
public partial class DistributionsTabView : UserControl
{
    public DistributionsTabView() => InitializeComponent();
}
