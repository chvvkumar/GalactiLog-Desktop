using Avalonia.Controls;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's Correlation tab body, bound to
/// <see cref="ViewModels.Analysis.CorrelationTabViewModel"/>. All the behaviour is on the
/// view-model; this class exists so the markup has a partial to compile into.
/// </summary>
public partial class CorrelationTabView : UserControl
{
    public CorrelationTabView() => InitializeComponent();
}
