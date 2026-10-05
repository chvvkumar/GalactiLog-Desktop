using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class IntegrationBarsPart : UserControl
{
    public static readonly StyledProperty<bool> IsFullProperty =
        AvaloniaProperty.Register<IntegrationBarsPart, bool>(nameof(IsFull));

    /// <summary>A segment's width: its fraction of the track, less the 1 px gap that follows it.</summary>
    public static readonly IMultiValueConverter SegmentWidth =
        new FuncMultiValueConverter<double, double>(values =>
        {
            var v = values.ToList();
            return v.Count == 2 ? Math.Max(0d, v[0] * v[1] - 1d) : 0d;
        });

    /// <summary>The goal tick's left offset on the track.</summary>
    public static readonly IMultiValueConverter GoalOffset =
        new FuncMultiValueConverter<double, Thickness>(values =>
        {
            var v = values.ToList();
            return v.Count == 2 ? new Thickness(Math.Max(0d, v[0] * v[1] - 1d), 0, 0, 0) : default;
        });

    public IntegrationBarsPart() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFullProperty)
        {
            SmallForm.IsVisible = !IsFull;
            FullForm.IsVisible = IsFull;
        }
    }

    /// <summary>True draws one 30 px row per filter with a night-segmented bar, the goal tick and the
    /// short-by text; false draws the small bars.</summary>
    public bool IsFull
    {
        get => GetValue(IsFullProperty);
        set => SetValue(IsFullProperty, value);
    }
}
