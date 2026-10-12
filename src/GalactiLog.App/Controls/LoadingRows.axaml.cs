using Avalonia;
using Avalonia.Controls;
using GalactiLog.App.Controls.Table;

namespace GalactiLog.App.Controls;

/// <summary>N pulsing placeholder bars, each the table row height, shown while a list loads.
/// </summary>
public partial class LoadingRows : UserControl
{
    /// <summary>How many bars to draw. Eight by default.</summary>
    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<LoadingRows, int>(nameof(Rows), 8);

    public LoadingRows()
    {
        InitializeComponent();
        Build();
    }

    /// <inheritdoc cref="RowsProperty"/>
    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            Build();
        }
    }

    private void Build()
    {
        var panel = this.FindControl<StackPanel>("BarsPanel");
        if (panel is null)
        {
            return;
        }

        panel.Children.Clear();
        for (var i = 0; i < Rows; i++)
        {
            // The row height is the table's own, so the placeholder occupies what the rows will.
            // Feedback.axaml's 4 px vertical margin is taken out of it, leaving the gap between
            // bars inside the row rather than adding to it.
            var bar = new Border { Height = TableMetrics.RowMinHeight - 8 };
            bar.Classes.Add("loading-bar");
            panel.Children.Add(bar);
        }
    }
}
