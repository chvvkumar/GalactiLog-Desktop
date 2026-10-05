using Avalonia.Controls;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class CompareTablePart : UserControl
{
    private const double FilterColumn = 110d;

    public CompareTablePart() => InitializeComponent();

    // A grid column definition has no visual parent to bind through, so the pinned column follows
    // the text size here.
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty && this.FindControl<Grid>("CompareTable") is { } table)
            table.ColumnDefinitions[0].Width = new GridLength(FontScale.Of(FilterColumn, FontSize));
    }
}
