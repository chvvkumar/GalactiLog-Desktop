using Avalonia.Controls;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's Time Series tab body, bound to
/// <see cref="ViewModels.Analysis.TimeSeriesTabViewModel"/>: the metric picker, the smoothing
/// segment and the nightly trend chart. All the behaviour is on the view-model; this class exists
/// so the markup has a partial to compile into.
/// </summary>
public partial class TimeSeriesTabView : UserControl
{
    public TimeSeriesTabView() => InitializeComponent();
}
