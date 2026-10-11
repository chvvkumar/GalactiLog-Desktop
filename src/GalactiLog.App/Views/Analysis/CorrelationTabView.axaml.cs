using Avalonia.Controls;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's Correlation tab body, bound to
/// <see cref="ViewModels.Analysis.CorrelationTabViewModel"/>. All the behaviour is on the
/// view-model; the chart's height is <see cref="Controls.FillViewport"/>, set in the markup.
/// </summary>
public partial class CorrelationTabView : UserControl
{
    public CorrelationTabView() => InitializeComponent();
}
