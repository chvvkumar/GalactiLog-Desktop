using Avalonia;
using Avalonia.Controls;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class TrendChartPart : UserControl
{
    /// <summary>The laned form, set by the layout and never stored.</summary>
    public static readonly StyledProperty<bool> IsLanedProperty =
        AvaloniaProperty.Register<TrendChartPart, bool>(nameof(IsLaned));

    public TrendChartPart() => InitializeComponent();

    public bool IsLaned
    {
        get => GetValue(IsLanedProperty);
        set => SetValue(IsLanedProperty, value);
    }
}
