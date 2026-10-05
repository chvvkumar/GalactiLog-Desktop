using Avalonia;
using Avalonia.Controls;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Controls;

/// <summary>
/// Spec 13's Target detail metric chart, bound to
/// <see cref="ViewModels.TargetDetail.MetricChartViewModel"/> and therefore serving both the
/// cross-session and the per-session chart. All behaviour is in the view-model.
/// </summary>
public partial class MetricChartView : UserControl
{
    /// <summary>The laned form, set by the hosting part and handed to the view-model.</summary>
    public static readonly StyledProperty<bool> IsLanedProperty =
        AvaloniaProperty.Register<MetricChartView, bool>(nameof(IsLaned));

    /// <summary>Content the host places at the end of the pill row.</summary>
    public static readonly StyledProperty<object?> PillRowEndProperty =
        AvaloniaProperty.Register<MetricChartView, object?>(nameof(PillRowEnd));

    public MetricChartView() => InitializeComponent();

    public object? PillRowEnd
    {
        get => GetValue(PillRowEndProperty);
        set => SetValue(PillRowEndProperty, value);
    }

    public bool IsLaned
    {
        get => GetValue(IsLanedProperty);
        set => SetValue(IsLanedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if ((change.Property == IsLanedProperty || change.Property == DataContextProperty)
            && DataContext is MetricChartViewModel chart)
        {
            chart.IsLaned = IsLaned;
        }
    }
}
